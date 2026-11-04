using NexoRuta.Application.Reparto.Estimaciones;
namespace NexoRuta.UnitTests.Reparto;

public sealed class EstimadorRutaInicialTests
{
    private static readonly DateTimeOffset Ahora = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
    private static readonly DateTimeOffset Salida = DateTimeOffset.Parse("2026-11-02T02:50:00Z");
    private static SolicitudEstimacionInicial Pedido() => new(Guid.NewGuid(), Guid.NewGuid(), 4, 2,
        new(-34.9, -56.2), true, Salida, "America/Montevideo", "perfil-controlado", 7,
        [new(Guid.NewGuid(),1,true,new(-34.8,-56.1),true,10,Salida.AddHours(4)),
         new(Guid.NewGuid(),2,true,new(-34.7,-56.0),true,5,Salida.AddHours(5))]);

    [Fact]
    public async Task SequentialTimesIncludeEveryServiceAndReturnOnceWithoutReplacingManualTimes()
    {
        var pedido = Pedido(); var proveedor = new Controlado();
        var result = await new EstimadorRutaInicial(proveedor,new Reloj()).CalcularAsync(pedido,default);
        Assert.Equal(EstadoEstimacionInicial.Calculada,result.Estado);
        Assert.Equal(Salida.AddMinutes(10),result.Paradas[0].LlegadaCalculadaEnUtc);
        Assert.Equal(Salida.AddMinutes(40),result.Paradas[1].LlegadaCalculadaEnUtc);
        Assert.Equal(Salida.AddMinutes(60),result.RegresoEstimadoBaseEnUtc);
        Assert.Equal(Salida.AddMinutes(67),result.ListoPrevistoEnUtc);
        Assert.Equal(pedido.Paradas.Select(p=>p.LlegadaManualEnUtc),result.Paradas.Select(p=>p.LlegadaManualEnUtc));
        Assert.Equal(new[]{Salida,Salida.AddMinutes(20),Salida.AddMinutes(45)},proveedor.Pedidos.Select(p=>p.SalidaEnUtc));
        Assert.Equal(pedido.Base,proveedor.Pedidos.Last().Destino); Assert.Equal(3,result.Tramos.Count);
        Assert.All(result.Tramos,t=>Assert.Equal(FuenteVial.RespuestaControlada,t.Procedencia.Fuente));
        Assert.Equal(Ahora,result.CalculadoEnUtc); Assert.Equal(Ahora.AddMinutes(30),result.ValidoHastaUtc);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDataOrProviderCannotInventZeroTravelTimes(bool missingData)
    {
        var request = Pedido(); if(missingData)request=request with {BaseConfirmada=false};
        var result=await new EstimadorRutaInicial(null,new Reloj()).CalcularAsync(request,default);
        Assert.Equal(missingData?EstadoEstimacionInicial.DatosInsuficientes:EstadoEstimacionInicial.ProveedorNoConfigurado,result.Estado);
        Assert.Empty(result.Paradas);Assert.Null(result.RegresoEstimadoBaseEnUtc);Assert.Null(result.CalculadoEnUtc);
    }
    [Theory]
    [InlineData(EstadoTramoVial.RutaNoEncontrada,EstadoEstimacionInicial.RutaNoEncontrada)]
    [InlineData(EstadoTramoVial.ProveedorNoDisponible,EstadoEstimacionInicial.ProveedorNoDisponible)]
    [InlineData(EstadoTramoVial.TiempoAgotado,EstadoEstimacionInicial.TiempoAgotado)]
    [InlineData(EstadoTramoVial.CuotaExcedida,EstadoEstimacionInicial.CuotaExcedida)]
    public async Task TypedFailuresDiscardTheWholePartialItinerary(EstadoTramoVial error,EstadoEstimacionInicial expected)
    {
        var provider=new Controlado {Cambiar=r=>r.Solicitud.Indice==1?r with {Estado=error}:r};
        var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(Pedido(),default);
        Assert.Equal(expected,result.Estado);Assert.Equal(2,provider.Pedidos.Count);
        Assert.Empty(result.Paradas);Assert.Empty(result.Tramos);Assert.Null(result.ListoPrevistoEnUtc);Assert.Null(result.RegresoEstimadoBaseEnUtc);
    }
    [Theory]
    [InlineData(0)] // Missing service
    [InlineData(1)] // Invalid coordinates
    [InlineData(2)] // Unconfirmed stop
    [InlineData(3)] // Duplicate shipment
    [InlineData(4)] // Duplicate order
    [InlineData(5)] // Missing departure
    [InlineData(6)] // Unknown zone
    [InlineData(7)] // Missing profile
    [InlineData(8)] // Missing margin
    [InlineData(9)] // No authorized operator identity
    public async Task InsufficientDataNeverCallsTheProvider(int variant)
    {
        var s=Pedido();s=variant switch
        {
            0=>s with {Paradas=[s.Paradas[0] with {ServicioMinutos=null},s.Paradas[1]]},
            1=>s with {Base=new(double.NaN,0)},
            2=>s with {Paradas=[s.Paradas[0] with {PuntoConfirmado=false},s.Paradas[1]]},
            3=>s with {Paradas=[s.Paradas[0],s.Paradas[1] with {EnvioId=s.Paradas[0].EnvioId}]},
            4=>s with {Paradas=[s.Paradas[0],s.Paradas[1] with {Orden=1}]},
            5=>s with {SalidaPlanificadaEnUtc=null},6=>s with {ZonaHoraria="Not/AZone"},7=>s with {PerfilVial=null},
            8=>s with {MargenOperativoMinutos=null},_=>s with {OperadorId=Guid.Empty}
        };
        var provider=new Controlado();var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(s,default);
        Assert.Equal(EstadoEstimacionInicial.DatosInsuficientes,result.Estado);Assert.Empty(provider.Pedidos);Assert.Null(result.RegresoEstimadoBaseEnUtc);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task InvalidOrStaleProviderResponsesCannotPublishAnItinerary(int variant)
    {
        var provider=new Controlado {Cambiar=r=>variant switch
        {
            0=>r with {DuracionSegundos=double.NaN},1=>r with {DistanciaMetros=-1},2=>r with {DuracionSegundos=double.MaxValue},
            3=>r with {Solicitud=r.Solicitud with {Huella="obsolete"}},4=>r with {Solicitud=r.Solicitud with {Indice=99}},
            5=>r with {Procedencia=r.Procedencia! with {CalculadoEnUtc=Ahora.AddHours(1)}},
            6=>r with {DistanciaMetros=double.MaxValue},
            7=>r with {Solicitud=r.Solicitud with {PerfilVial="different-profile"}},
            _=>r with {Solicitud=r.Solicitud with {Destino=new(0,0)}}
        }};
        var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(Pedido(),default);
        Assert.Equal(EstadoEstimacionInicial.RespuestaInvalida,result.Estado);Assert.Empty(result.Paradas);Assert.Empty(result.Tramos);
    }
    [Fact]
    public async Task FreshnessIsExplicitAndAnotherTenantRouteOrChangedInputInvalidatesTheResult()
    {
        var request=Pedido();var before=System.Text.Json.JsonSerializer.Serialize(request);
        var result=await new EstimadorRutaInicial(new Controlado(),new Reloj()).CalcularAsync(request,default);
        Assert.Equal(VigenciaEstimacion.Vigente,EstimadorRutaInicial.ObtenerVigencia(result,request,Ahora));
        Assert.Equal(VigenciaEstimacion.Vencida,EstimadorRutaInicial.ObtenerVigencia(result,request,Ahora.AddMinutes(30)));
        foreach(var changed in new[]{request with {OperadorId=Guid.NewGuid()},request with {RutaId=Guid.NewGuid()},request with {VersionRuta=5},
            request with {VersionBase=3},request with {Base=new(-34.6,-56)},request with {SalidaPlanificadaEnUtc=Salida.AddHours(1)},
            request with {MargenOperativoMinutos=8},request with {Paradas=[request.Paradas[0] with {ServicioMinutos=11},request.Paradas[1]]},
            request with {Paradas=[request.Paradas[0] with {Activa=false},request.Paradas[1]]}})
            Assert.Equal(VigenciaEstimacion.Invalidada,EstimadorRutaInicial.ObtenerVigencia(result,changed,Ahora));
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(request));
    }
    [Fact]
    public async Task MissingExpiryIsUnknownAndOldMetadataStaysExpiredRatherThanBeingRefreshedByReceipt()
    {
        var request=Pedido();
        foreach(var expiry in new DateTimeOffset?[]{null,Ahora.AddMinutes(-10)})
        {
            var provider=new Controlado {Cambiar=r=>r with {Procedencia=r.Procedencia! with {CalculadoEnUtc=Ahora.AddMinutes(-30),ValidoHastaUtc=expiry}}};
            var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(request,default);
            Assert.Equal(expiry is null?VigenciaEstimacion.Desconocida:VigenciaEstimacion.Vencida,EstimadorRutaInicial.ObtenerVigencia(result,request,Ahora));
        }
    }
    [Fact]
    public async Task SuspendedStopsAreExcludedAndNoStopsDoesNotBecomeAFictitiousReturn()
    {
        var s=Pedido();var provider=new Controlado();
        var inactive=s.Paradas[0] with {EnvioId=Guid.NewGuid(),Orden=3,Activa=false,Punto=new(double.NaN,double.PositiveInfinity),ServicioMinutos=null};
        var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(s with {Paradas=[s.Paradas[1],inactive,s.Paradas[0]]},default);
        Assert.Equal(EstadoEstimacionInicial.Calculada,result.Estado);Assert.Equal(s.Paradas.Select(p=>p.EnvioId),result.Paradas.Select(p=>p.EnvioId));Assert.Equal(3,provider.Pedidos.Count);
        provider.Pedidos.Clear();var empty=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(s with {Paradas=[inactive],BaseConfirmada=false,Base=null},default);
        Assert.Equal(EstadoEstimacionInicial.SinParadas,empty.Estado);Assert.Empty(provider.Pedidos);Assert.Null(empty.RegresoEstimadoBaseEnUtc);
    }
    [Fact]
    public async Task CallerCollectionChangesDuringTheCalculationCannotChangeTheFrozenRequest()
    {
        var s=Pedido();var mutable=s.Paradas.ToArray();var originalSecond=mutable[1];
        var provider=new Controlado {Cambiar=r=>{mutable[1]=mutable[1] with {ServicioMinutos=999,Punto=new(1,1)};return r;}};
        var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(s with {Paradas=mutable},default);
        Assert.Equal(Salida.AddMinutes(60),result.RegresoEstimadoBaseEnUtc);Assert.Equal(originalSecond.Punto,provider.Pedidos[1].Destino);
    }
    [Fact]
    public async Task CancellationPropagatesButNetworkFailureAndTimeoutAreControlledWithoutLeakingDetails()
    {
        using var cts=new CancellationTokenSource();cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new EstimadorRutaInicial(new Controlado(),new Reloj()).CalcularAsync(Pedido(),cts.Token));
        foreach(var (error,state) in new (Exception,EstadoEstimacionInicial)[]{(new HttpRequestException("private upstream details"),EstadoEstimacionInicial.ProveedorNoDisponible),(new OperationCanceledException(),EstadoEstimacionInicial.TiempoAgotado)})
        {
            var provider=new Controlado {Cambiar=_=>throw error};var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(Pedido(),default);
            Assert.Equal(state,result.Estado);Assert.Null(result.RegresoEstimadoBaseEnUtc);
        }
        using var during=new CancellationTokenSource();
        var cancelling=new Controlado {Cambiar=r=>{during.Cancel();return r;}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new EstimadorRutaInicial(cancelling,new Reloj()).CalcularAsync(Pedido(),during.Token));
        Assert.Single(cancelling.Pedidos);
    }
    [Fact]
    public async Task ExplicitZeroServiceMarginAndTravelAreNotConfusedWithMissingValues()
    {
        var s=Pedido();s=s with {MargenOperativoMinutos=0,Paradas=s.Paradas.Select(p=>p with {ServicioMinutos=0,Punto=s.Base}).ToArray()};
        var provider=new Controlado {Cambiar=r=>r with {DuracionSegundos=0,DistanciaMetros=0}};
        var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(s,default);
        Assert.Equal(EstadoEstimacionInicial.Calculada,result.Estado);Assert.Equal(Salida,result.RegresoEstimadoBaseEnUtc);Assert.Equal(Salida,result.ListoPrevistoEnUtc);
    }
    [Fact]
    public async Task MissingNumericFieldsCannotBecomeAZeroMinuteRoadLeg()
    {
        var provider=new Controlado {Cambiar=r=>new(r.Solicitud,EstadoTramoVial.Encontrado,Procedencia:r.Procedencia)};
        var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularAsync(Pedido(),default);
        Assert.Equal(EstadoEstimacionInicial.RespuestaInvalida,result.Estado);Assert.Null(result.RegresoEstimadoBaseEnUtc);
    }
    [Fact]
    public async Task RecorridoCanReturnOnlyAndAddsServiceOnlyForRemainingVisits()
    {
        var s=Pedido();var provider=new Controlado();var gps=new PuntoVial(-34.85,-56.15);
        var result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularRecorridoAsync(s with{Paradas=[]},gps,default);
        Assert.Equal(EstadoEstimacionInicial.Calculada,result.Estado);Assert.Empty(result.Paradas);Assert.Single(result.Tramos);
        Assert.Equal(gps,provider.Pedidos.Single().Origen);Assert.Equal(s.Base,provider.Pedidos.Single().Destino);
        Assert.Equal(Salida.AddMinutes(10),result.RegresoEstimadoBaseEnUtc);Assert.Equal(Salida.AddMinutes(17),result.ListoPrevistoEnUtc);
        provider=new Controlado();result=await new EstimadorRutaInicial(provider,new Reloj()).CalcularRecorridoAsync(s with{Paradas=[s.Paradas[1]]},gps,default);
        Assert.Single(result.Paradas);Assert.Equal(Salida.AddMinutes(35),result.RegresoEstimadoBaseEnUtc);Assert.Equal(Salida.AddMinutes(42),result.ListoPrevistoEnUtc);
    }
    private sealed class Reloj:TimeProvider { public override DateTimeOffset GetUtcNow()=>Ahora; }
    private sealed class Controlado: IProveedorTramosViales
    {
        public List<SolicitudTramoVial> Pedidos {get;}=[];
        public Func<RespuestaTramoVial,RespuestaTramoVial>? Cambiar {get;init;}
        public Task<RespuestaTramoVial> CalcularAsync(SolicitudTramoVial p,CancellationToken ct)
        {
            Pedidos.Add(p);var r=new RespuestaTramoVial(p,EstadoTramoVial.Encontrado,
                new[]{600d,1200d,900d}[p.Indice],1000,new("CONTROLADO","fixture",FuenteVial.RespuestaControlada,ModeloTrafico.SinTrafico,"Datos sintéticos",Ahora,Ahora.AddMinutes(30)));
            return Task.FromResult(Cambiar?.Invoke(r)??r);
        }
    }
}
