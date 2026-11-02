using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Envios.Contracts.Requests;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
namespace NexoRuta.IntegrationTests.Envios;

public sealed class TarifaAdmisionHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CrearEnvioRequest Solicitud(Guid operador) => new()
    {
        OperadorId=operador,DestinatarioNombre="Destino sintético CU09",DestinatarioEmail="cu09@example.test",Direccion="Dirección elegida por el comercio",
        Servicio=ServiciosEnvioTestData.Declarar(operador),
        Bultos=[new(){Codigo="CU09-UNO",PesoGramos=500,LargoCentimetros=20,AnchoCentimetros=10,AltoCentimetros=5},
            new(){Codigo="CU09-DOS",PesoGramos=1500,LargoCentimetros=30,AnchoCentimetros=20,AltoCentimetros=10}]
    };

    [Fact]
    public async Task AltaCalculaTodosLosBultosYGuardaCargoPuntoYProcedenciaConRolRestringido()
    {
        var data=await postgres.CreateDatabaseAsync();
        using(var seed=data.CreateShippingApiFactory()) using(seed.CreateHttpClient()){}
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        await using var db=data.CreateDbContext();var operador=await db.Operadores.Select(o=>o.Id).SingleAsync();
        var acceso=await db.AccesosUsuario.Where(a=>a.ComercioId!=null).Select(a=>a.Id).SingleAsync();await client.SetBearerForAccessAsync(data,acceso);
        var request=Solicitud(operador);
        using var quote=await client.PostAsJsonAsync("/api/envios/cotizacion",request);Assert.Equal(HttpStatusCode.OK,quote.StatusCode);
        var cotizacion=await quote.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(121.40m,cotizacion.GetProperty("totalFinalUyu").GetDecimal());
        request.Servicio.VersionTarifarioEsperada=cotizacion.GetProperty("versionTarifarioId").GetGuid();
        using var created=await client.PostAsJsonAsync("/api/envios",request);Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var result=await created.Content.ReadFromJsonAsync<JsonElement>();var envio=result.GetProperty("id").GetGuid();
        Assert.Equal(121.40m,result.GetProperty("tarifa").GetProperty("totalFinalUyu").GetDecimal());
        Assert.Equal(2,await db.Bultos.CountAsync());Assert.Equal(2,await db.OutboxMessages.CountAsync());
        var destino=await db.Direcciones.SingleAsync();Assert.Equal(-34.901,destino.Latitud);Assert.Equal(-56.201,destino.Longitud);Assert.NotNull(destino.ConfirmadaEnUtc);
        var detalle=await client.GetFromJsonAsync<JsonElement[]> ("/api/envios");Assert.Equal(121.40m,Assert.Single(detalle!).GetProperty("tarifa").GetProperty("totalFinalUyu").GetDecimal());
        using var tarifa=await client.GetAsync($"/api/envios/{envio}/tarifa");Assert.Equal(HttpStatusCode.OK,tarifa.StatusCode);
        var count=await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"TarifasEnvio\" WHERE \"EnvioId\"={envio}").SingleAsync();Assert.Equal(1,count);
    }

    [Theory]
    [InlineData(-30.9,-55.55,"Estandar",false)] // Rivera, outside the configured Montevideo geometry.
    [InlineData(-35,-56.4,"Estandar",true)] // Polygon boundary is covered.
    [InlineData(-35.000001,-56.4,"Estandar",false)]
    [InlineData(-34.901,-56.201,"Urgente",false)]
    public async Task HttpDirectoRespetaGeometriaLimitesYModalidad(double lat,double lon,string modalidad,bool permitido)
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        await using var db=data.CreateDbContext();var operador=await db.Operadores.Select(o=>o.Id).SingleAsync();await client.SetBearerForAccessAsync(data,await db.AccesosUsuario.Where(a=>a.ComercioId!=null).Select(a=>a.Id).SingleAsync());
        var request=Solicitud(operador);request.Servicio.Latitud=lat;request.Servicio.Longitud=lon;request.Servicio.Modalidad=modalidad;
        using var response=await client.PostAsJsonAsync("/api/envios",request);Assert.Equal(permitido?HttpStatusCode.Created:HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Equal(permitido?1:0,await db.Envios.CountAsync());Assert.Equal(permitido?2:0,await db.Bultos.CountAsync());Assert.Equal(permitido?2:0,await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task ZonaDeOtroOperadorYPrecioManipuladoNoSeAdmiten()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        await using var db=data.CreateDbContext();var operador=await db.Operadores.Select(o=>o.Id).SingleAsync();await client.SetBearerForAccessAsync(data,await db.AccesosUsuario.Where(a=>a.ComercioId!=null).Select(a=>a.Id).SingleAsync());
        var request=Solicitud(operador);request.Servicio.ZonaId=Guid.NewGuid();
        using var wrongZone=await client.PostAsJsonAsync("/api/envios",request);Assert.Equal(HttpStatusCode.BadRequest,wrongZone.StatusCode);
        request.Servicio.ZonaId=operador;
        var payload=JsonSerializer.SerializeToNode(request,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;payload["importe"]=0;
        using var tampered=await client.PostAsJsonAsync("/api/envios",payload);Assert.Equal(HttpStatusCode.BadRequest,tampered.StatusCode);
        Assert.Empty(await db.Envios.ToArrayAsync());
    }
    [Fact]
    public async Task PublicarOtraTarifaNoReescribeCargoYEditarDestinoConservaPublicacionOriginal()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        await using var db=data.CreateDbContext();var operador=await db.Operadores.Select(o=>o.Id).SingleAsync();var usuario=await db.AccesosUsuario.Where(a=>a.OperadorId==operador).Select(a=>a.UsuarioId).SingleAsync();
        await client.SetBearerForAccessAsync(data,await db.AccesosUsuario.Where(a=>a.ComercioId!=null).Select(a=>a.Id).SingleAsync());
        var request=Solicitud(operador);using var first=await client.PostAsJsonAsync("/api/envios",request);Assert.Equal(HttpStatusCode.Created,first.StatusCode);
        var body=await first.Content.ReadFromJsonAsync<JsonElement>();var envio=body.GetProperty("id").GetGuid();var original=body.GetProperty("tarifa").GetProperty("versionTarifarioId").GetGuid();
        await using var publicacion=await db.Database.BeginTransactionAsync();
        var root=await db.TarifariosOperador.SingleAsync();var nueva=NexoRuta.Domain.Tarifas.VersionTarifario.CrearBorrador(operador,usuario);
        db.VersionesTarifario.Add(nueva);await db.SaveChangesAsync();
        nueva.ReemplazarConfiguracion(0,[new(operador,NexoRuta.Domain.Tarifas.ModalidadTarifaria.Estandar,200m,10m,200m,
            [new("Recargo sintético",NexoRuta.Domain.Tarifas.TipoAjusteTarifario.RecargoFijo,ImporteFijoUyu:5m),
             new("Bonificación sintética",NexoRuta.Domain.Tarifas.TipoAjusteTarifario.BonificacionPorcentual,Porcentaje:10m)])]);
        await db.SaveChangesAsync();nueva.Publicar(nueva.VersionConcurrencia,root.UltimoNumeroPublicacion+1,usuario,new HashSet<Guid>{operador});await db.SaveChangesAsync();root.Publicar(nueva);await db.SaveChangesAsync();await publicacion.CommitAsync();await publicacion.DisposeAsync();
        var actual=await client.GetFromJsonAsync<JsonElement>($"/api/envios/{envio}/tarifa");Assert.Equal(original,actual.GetProperty("versionTarifarioId").GetGuid());Assert.Equal(121.40m,actual.GetProperty("totalFinalUyu").GetDecimal());
        using var editar=await client.PutAsJsonAsync($"/api/envios/{envio}/destino",new{Version=1,Latitud=-34.902,Longitud=-56.202,Direccion="Destino ajustado"});Assert.Equal(HttpStatusCode.NoContent,editar.StatusCode);
        var despues=await client.GetFromJsonAsync<JsonElement>($"/api/envios/{envio}/tarifa");Assert.Equal(original,despues.GetProperty("versionTarifarioId").GetGuid());Assert.Equal(121.40m,despues.GetProperty("totalFinalUyu").GetDecimal());
        using var rivera=await client.PutAsJsonAsync($"/api/envios/{envio}/destino",new{Version=2,Latitud=-30.9,Longitud=-55.55,Direccion="Rivera fuera de cobertura"});Assert.Equal(HttpStatusCode.Conflict,rivera.StatusCode);
        var direccion=await db.Direcciones.AsNoTracking().SingleAsync();Assert.Equal("Destino ajustado",direccion.Descripcion);Assert.Equal(2,direccion.Version);
        Assert.Equal(2,await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"TarifasEnvio\" WHERE \"EnvioId\"={envio}").SingleAsync());
        request.Servicio.VersionTarifarioEsperada=original;using var stale=await client.PostAsJsonAsync("/api/envios",request);Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        request.Servicio.VersionTarifarioEsperada=null;using var current=await client.PostAsJsonAsync("/api/envios",request);Assert.Equal(HttpStatusCode.Created,current.StatusCode);
        var newer=await current.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(nueva.Id,newer.GetProperty("tarifa").GetProperty("versionTarifarioId").GetGuid());Assert.Equal(204.26m,newer.GetProperty("tarifa").GetProperty("totalFinalUyu").GetDecimal());
        var denied=await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.\"TarifasEnvio\" SET \"Resultado\"='{{}}'::jsonb WHERE \"EnvioId\"={envio}"));Assert.Equal("55000",denied.SqlState);
    }

    [Fact]
    public async Task ImportacionValidaTodosLosDestinosAntesDeEscribirYReimportacionConservaCargo()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        await using var db=data.CreateDbContext();var operador=await db.Operadores.Select(o=>o.Id).SingleAsync();await client.SetBearerForAccessAsync(data,await db.AccesosUsuario.Where(a=>a.ComercioId!=null).Select(a=>a.Id).SingleAsync());
        const string header="referenciaExterna,nombreDestinatario,emailDestinatario,direccion,codigoBulto,pesoGramos,largoCentimetros,anchoCentimetros,altoCentimetros,zonaId,latitud,longitud,modalidad";
        var buena="VALIDA,Nombre,nombre@example.test,Dirección,UNO,500,20,10,5,,-34.901,-56.201,Estandar";
        var segunda="VALIDA,Nombre,nombre@example.test,Dirección,DOS,1500,30,20,10,,-34.901,-56.201,Estandar";
        var mala="RIVERA,Nombre,nombre@example.test,Rivera,TRES,500,20,10,5,,-30.9,-55.55,Estandar";
        async Task<HttpResponseMessage> Importar(string filas,int formato=2)
        {
            using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(header+"\n"+filas)),"archivo","cu09.csv");form.Add(new StringContent(operador.ToString()),"operadorId");form.Add(new StringContent(formato.ToString()),"versionFormato");return await client.PostAsync("/api/envios/importaciones",form);
        }
        using var rejected=await Importar(buena+"\n"+mala);Assert.Equal(HttpStatusCode.UnprocessableEntity,rejected.StatusCode);
        Assert.Empty(await db.Envios.ToArrayAsync());Assert.Empty(await db.Bultos.ToArrayAsync());Assert.Empty(await db.IdentidadesEnvioImportado.ToArrayAsync());Assert.Empty(await db.OutboxMessages.ToArrayAsync());
        using var accepted=await Importar(buena+"\n"+segunda);Assert.Equal(HttpStatusCode.OK,accepted.StatusCode);Assert.Single(await db.Envios.ToArrayAsync());Assert.Equal(2,await db.Bultos.CountAsync());Assert.Equal((short)2,(await db.IdentidadesEnvioImportado.SingleAsync()).VersionHuella);
        using var retry=await Importar(segunda+"\n"+buena);Assert.Equal(HttpStatusCode.OK,retry.StatusCode);Assert.Single(await db.Envios.ToArrayAsync());Assert.Equal(2,await db.OutboxMessages.CountAsync());
        Assert.Equal(1,await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"TarifasEnvio\"").SingleAsync());
        using var oldFormat=await Importar(buena,formato:1);Assert.Equal(HttpStatusCode.BadRequest,oldFormat.StatusCode);Assert.Single(await db.Envios.ToArrayAsync());
    }

    [Fact]
    public async Task ZonaActivaSinPublicacionNoInventaImporteYRolesNoAutorizadosNoCotizan()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateApiFactory())using(seed.CreateHttpClient()){}
        await using var db=data.CreateDbContext();var operador=await db.Operadores.Select(o=>o.Id).SingleAsync();
        var gf=NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid:4326);
        var zona=NexoRuta.Domain.ZonasCobertura.ZonaCobertura.Crear(operador,"Zona sin tarifa",gf.CreateMultiPolygon([gf.CreatePolygon([new(-56.4,-35),new(-56,-35),new(-56,-34.7),new(-56.4,-34.7),new(-56.4,-35)])]),id:operador,
            franjasIniciales:[new(NexoRuta.Domain.ZonasCobertura.DiaSemanaIso.Lunes,new TimeOnly(9,0),new TimeOnly(18,0))]);zona.Activar(zona.Version);db.ZonasCobertura.Add(zona);await db.SaveChangesAsync();
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        using var anonymous=await client.PostAsJsonAsync("/api/envios/cotizacion",Solicitud(operador));Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        await client.SetBearerForAccessAsync(data,await db.AccesosUsuario.Where(a=>a.OperadorId!=null).Select(a=>a.Id).SingleAsync());using var forbidden=await client.PostAsJsonAsync("/api/envios/cotizacion",Solicitud(operador));Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
        await client.SetBearerForAccessAsync(data,await db.AccesosUsuario.Where(a=>a.ComercioId!=null).Select(a=>a.Id).SingleAsync());using var missing=await client.PostAsJsonAsync("/api/envios",Solicitud(operador));Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);Assert.Empty(await db.Envios.ToArrayAsync());
    }
    [Fact]
    public async Task CargosSonAisladosPorComercioYOperadorYReferenciasV1NoSeReinterpretan()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        await using var db=data.CreateDbContext();var operador=await db.Operadores.Select(o=>o.Id).SingleAsync();var accesoA=await db.AccesosUsuario.Where(a=>a.ComercioId!=null).Select(a=>a.Id).SingleAsync();
        await AuthenticationTestData.Seeder(db,new NexoRuta.Infrastructure.Administracion.AccesoInicial("cu09-b@example.test","Operador B CU09","Comercio B CU09","cu09-op-b@example.test")).SeedAsync();await ReglasEnvioTestData.SembrarInicialesAsync(db);
        var operadorB=await db.Operadores.Where(o=>o.Id!=operador).Select(o=>o.Id).SingleAsync();var comercioB=await db.Comercios.Where(c=>c.Nombre=="Comercio B CU09").Select(c=>c.Id).SingleAsync();var accesoB=await db.AccesosUsuario.Where(a=>a.ComercioId==comercioB).Select(a=>a.Id).SingleAsync();
        await ServiciosEnvioTestData.PublicarServicioAsync(db,operadorB,await db.AccesosUsuario.Where(a=>a.OperadorId==operadorB).Select(a=>a.UsuarioId).SingleAsync(),importeBase:250m);
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();await client.SetBearerForAccessAsync(data,accesoA);
        using var create=await client.PostAsJsonAsync("/api/envios",Solicitud(operador));Assert.Equal(HttpStatusCode.Created,create.StatusCode);var body=await create.Content.ReadFromJsonAsync<JsonElement>();var envio=body.GetProperty("id").GetGuid();var comercio=body.GetProperty("comercioId").GetGuid();
        var hash=new byte[32];await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.\"IdentidadesEnvioImportado\" (\"ComercioId\",\"ReferenciaExterna\",\"EnvioId\",\"OperadorId\",\"VersionHuella\",\"Huella\") VALUES ({comercio},'LEGADO-CU09',{envio},{operador},1,{hash})");
        const string csv="referenciaExterna,nombreDestinatario,emailDestinatario,direccion,codigoBulto,pesoGramos,largoCentimetros,anchoCentimetros,altoCentimetros,zonaId,latitud,longitud,modalidad\nLEGADO-CU09,Nombre,nombre@example.test,Dirección,UNO,500,20,10,5,,-34.901,-56.201,Estandar";
        using var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)),"archivo","legado.csv");form.Add(new StringContent(operador.ToString()),"operadorId");form.Add(new StringContent("2"),"versionFormato");
        using var legacy=await client.PostAsync("/api/envios/importaciones",form);Assert.Equal(HttpStatusCode.Conflict,legacy.StatusCode);Assert.Contains("OriginalDeclarationVersionMismatch",await legacy.Content.ReadAsStringAsync());Assert.Single(await db.Envios.ToArrayAsync());Assert.Equal((short)1,(await db.IdentidadesEnvioImportado.SingleAsync()).VersionHuella);
        using var own=await client.GetAsync($"/api/envios/{envio}/tarifa/historial");Assert.Equal(HttpStatusCode.OK,own.StatusCode);
        await client.SetBearerForAccessAsync(data,accesoB);using var hidden=await client.GetAsync($"/api/envios/{envio}/tarifa");Assert.Equal(HttpStatusCode.NotFound,hidden.StatusCode);using var hiddenHistory=await client.GetAsync($"/api/envios/{envio}/tarifa/historial");Assert.Equal(HttpStatusCode.NotFound,hiddenHistory.StatusCode);
        var wrong=Solicitud(operadorB);wrong.Servicio.ZonaId=operador;using var wrongZone=await client.PostAsJsonAsync("/api/envios",wrong);Assert.Equal(HttpStatusCode.BadRequest,wrongZone.StatusCode);
        wrong.Servicio.ZonaId=operadorB;using var allowedB=await client.PostAsJsonAsync("/api/envios",wrong);Assert.Equal(HttpStatusCode.Created,allowedB.StatusCode);var cargoB=await allowedB.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(operadorB,cargoB.GetProperty("operadorId").GetGuid());Assert.Equal(271.40m,cargoB.GetProperty("tarifa").GetProperty("totalFinalUyu").GetDecimal());
        await client.SetBearerForAccessAsync(data,await db.AccesosUsuario.Where(a=>a.OperadorId==operadorB).Select(a=>a.Id).SingleAsync());using var operatorHidden=await client.GetAsync($"/api/envios/{envio}/tarifa");Assert.Equal(HttpStatusCode.NotFound,operatorHidden.StatusCode);
    }
    [Fact]
    public async Task ZonaInactivaNoSeHabilitaParaAceptarElPedido()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        await using var db=data.CreateDbContext();var zona=await db.ZonasCobertura.SingleAsync();zona.Desactivar(zona.Version);await db.SaveChangesAsync();
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        await client.SetBearerForAccessAsync(data,await db.AccesosUsuario.Where(a=>a.ComercioId!=null).Select(a=>a.Id).SingleAsync());using var response=await client.PostAsJsonAsync("/api/envios",Solicitud(zona.OperadorId));Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Empty(await db.Envios.ToArrayAsync());Assert.Equal(NexoRuta.Domain.ZonasCobertura.EstadoZonaCobertura.Inactiva,(await db.ZonasCobertura.AsNoTracking().SingleAsync()).Estado);
    }
}
