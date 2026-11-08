using NexoRuta.Domain.Tarifas;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Gestion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
namespace NexoRuta.IntegrationTests.Gestion;

public sealed class GestionCircuitTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    internal static string Period=>"desde=2026-11-01T03:00:00Z&hasta="+Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
    [Fact]
    public async Task MigracionesAditivasReconcilianHechosExistentesSinCambiarSusCargos()
    {
        var data=await postgres.CreateDatabaseAsync("20261106190700_PublicEstimatedServiceWindow");
        using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        await using var db=data.CreateDbContext();var op=await db.Operadores.SingleAsync();var commerce=await db.AccesosUsuario.SingleAsync(x=>x.ComercioId!=null);
        var recipient=new NexoRuta.Domain.Envios.Destinatario(op.Id,"Anterior sintético","anterior@example.test");var address=new NexoRuta.Domain.Envios.Direccion(op.Id,"Anterior 123");
        var rule=(await db.ReglasOperativasOperadores.SingleAsync()).VersionVigenteId!.Value;
        var shipment=new NexoRuta.Domain.Envios.Envio(op.Id,commerce.ComercioId!.Value,commerce.UsuarioId,recipient.Id,address.Id,rule);
        var parcel=new NexoRuta.Domain.Envios.Bulto(op.Id,shipment.Id,"ANTERIOR",1000,10,10,10);db.AddRange(recipient,address,shipment,parcel);await db.SaveChangesAsync();await ServiciosEnvioTestData.AdjuntarCargoAsync(db,shipment,[parcel]);
        await db.Database.MigrateAsync();
        Assert.Equal(1,await db.Database.SqlQueryRaw<int>("SELECT nexoruta_app.reconcile_management(NULL,NULL,100) AS \"Value\"").SingleAsync());
        Assert.Equal(110.20m,await db.Database.SqlQueryRaw<decimal>("SELECT sum(\"ImporteUyu\") AS \"Value\" FROM public.\"MovimientosCuenta\"").SingleAsync());
        Assert.Equal(shipment.Id,await db.Database.SqlQueryRaw<Guid>("SELECT \"EnvioId\" AS \"Value\" FROM public.\"GestionOperaciones\"").SingleAsync());
    }
    [Fact]
    public async Task FranjasCapturadasRespetanMedianocheLocalYLimitesExclusivos()
    {
        var data=await postgres.CreateDatabaseAsync();await using var db=data.CreateDbContext();
        var snapshot="""{"zonaHoraria":"America/Montevideo","franjas":[{"estado":"Activa","diaSemanaIso":1,"horaInicio":"23:00:00","horaFin":"01:00:00"}]}""";
        // Monday 23:00 to Tuesday 01:00 local = Tuesday 02:00 to 04:00 UTC.
        foreach(var sample in new[]{(2,0,true),(3,30,true),(4,0,false),(1,59,false)})
        {
            var instant=new DateTimeOffset(2026,11,3,sample.Item1,sample.Item2,0,TimeSpan.Zero);
            var actual=await db.Database.SqlQuery<bool>($"SELECT nexoruta_app.management_in_slot({instant},{snapshot}::jsonb) AS \"Value\"").SingleAsync();
            Assert.Equal(sample.Item3,actual);
            Assert.Equal(new NexoRuta.Domain.Reparto.VentanaRuta(1,new TimeOnly(23,0),new TimeOnly(1,0)).Contiene(instant,0,TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")),actual);
        }
    }
    [Fact]
    public async Task HechosProyectadosDenominadoresHistoriaSaldosConfirmacionConcurrenteYCorreccion()
    {
        var s=await GestionScenario.CreateAsync(postgres);
        using var factory=new IntegrationApiFactory(s.Runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        var op=s.OperatorA.OperadorId!.Value;var commerce=s.CommerceA.ComercioId!.Value;var root=$"api/gestion/operadores/{op}";
        await client.SetBearerForAccessAsync(s.Data,s.OperatorA.Id);
        Assert.Equal(9,await s.ReconcileAsync());Assert.Equal(0,await s.ReconcileAsync());
        var report=(await client.GetFromJsonAsync<ReporteGestion>(root+"/reportes?"+Period))!;
        Assert.Equal(8,report.Total); // 7 commerce A + 1 commerce B, no event or visit multiplication.
        var deliveries=Assert.Single(report.Indicadores,i=>i.Corte=="Tipo" && i.Clave=="Entrega");
        Assert.Equal(7,deliveries.Cantidad);Assert.Equal(2,deliveries.Cumplidos);Assert.Equal(2,deliveries.Incumplidos);
        Assert.Equal(1,deliveries.SinCompromiso);Assert.Equal(1,deliveries.PruebaPendiente);Assert.Equal(1,deliveries.NoEvaluables);
        Assert.Equal(8,deliveries.Bultos);Assert.Equal(8m,deliveries.PesoKg);Assert.Equal(.008m,deliveries.VolumenM3);
        var pickups=Assert.Single(report.Indicadores,i=>i.Corte=="Tipo" && i.Clave=="Retiro");Assert.Equal(1,pickups.Cantidad);Assert.Equal(1,pickups.Cumplidos);Assert.Equal(0,pickups.Pendientes);
        Assert.Equal(1,Assert.Single(report.Motivos,m=>m.Tipo=="IntentoFallido").Cantidad);
        Assert.Equal(1,Assert.Single(report.Motivos,m=>m.Tipo=="PendienteBulto").Cantidad);
        var driver=Assert.Single(report.Indicadores,i=>i.Corte=="Repartidor" && i.Clave==s.SecondDriver.ToString());Assert.Equal(1,driver.Cantidad);Assert.Equal(1,driver.Incumplidos);
        var detail=(await client.GetFromJsonAsync<DetalleGestion>(root+$"/reportes/operaciones/{s.PartialShipment}"))!;
        Assert.Equal(2,detail.Bultos.Count);Assert.Contains(detail.Bultos,x=>x.RepartidorId==s.OperatorA.Id);Assert.Contains(detail.Bultos,x=>x.RepartidorId==s.SecondDriver);
        // A UTC calendar day would include Nov 1 23:30 local. The correct Nov 2 boundary excludes every fixture admission.
        var boundary=(await client.GetFromJsonAsync<ReporteGestion>(root+"/reportes?desde=2026-11-02T03:00:00Z&hasta=2026-11-03T03:00:00Z"))!;Assert.Equal(0,boundary.Total);
        var page=(await client.GetFromJsonAsync<ReporteGestion>(root+"/reportes?"+Period+"&tamano=2&pagina=2"))!;Assert.Equal(2,page.Detalles.Count);Assert.DoesNotContain(page.Detalles,x=>x.EnvioId==report.Detalles[0].EnvioId);
        var account=(await client.GetFromJsonAsync<CuentaGestion>(root+"/cuenta?"+Period+$"&comercioId={commerce}"))!;
        // Independent arithmetic: 110.20 + (120.40+25) + 110.20 + 110.20 + 75.10 + 110.20 = 661.30.
        Assert.Equal(661.30m,account.SaldoOperativoUyu);Assert.Equal(330.70m,account.PendienteLiquidarUyu);
        Assert.Equal(25m,Assert.Single(account.Movimientos,x=>x.Concepto=="Recargo nueva visita").ImporteUyu);
        // Publishing new prices/rules and changing today's courier profile cannot rewrite captured commitments/amounts/authorship.
        await using(var owner=s.Data.CreateDbContext())
        {
            await using var publication=await owner.Database.BeginTransactionAsync();
            var prices=await owner.TarifariosOperador.SingleAsync(x=>x.OperadorId==op);
            var v=VersionTarifario.CrearBorrador(op,s.OperatorA.UsuarioId);owner.Add(v);await owner.SaveChangesAsync();
            v.ReemplazarConfiguracion(0,[new(op,ModalidadTarifaria.Estandar,900,90,900,[]),new(op,ModalidadTarifaria.Retiro,700,70,700,[])]);await owner.SaveChangesAsync();
            v.Publicar(v.VersionConcurrencia,prices.UltimoNumeroPublicacion+1,s.OperatorA.UsuarioId,new HashSet<Guid>{op});await owner.SaveChangesAsync();prices.Publicar(v);await owner.SaveChangesAsync();
            var rules=await owner.ReglasOperativasOperadores.SingleAsync(x=>x.OperadorId==op);
            var next=VersionReglasOperativas.CrearBorrador(op,s.OperatorA.UsuarioId);owner.Add(next);await owner.SaveChangesAsync();
            next.ReemplazarConfiguracion(0,new(3,1,PoliticaDevolucionAlAgotarIntentos.DecisionOperativa,10,5,60,[TipoPruebaEntrega.Fotografia],[new("AUSENTE","Nueva descripción",true)]){PermiteEntregaParcial=true,PoliticaPendientesParcial=PoliticaPendientesParcial.NuevaVisitaConRecargo,RecargoNuevaVisitaUyu=99});await owner.SaveChangesAsync();
            next.Publicar(next.VersionConcurrencia,rules.UltimoNumeroPublicacion+1,s.OperatorA.UsuarioId);await owner.SaveChangesAsync();rules.Publicar(next);await owner.SaveChangesAsync();
            var courier=await owner.Repartidores.SingleAsync(x=>x.AccesoId==s.SecondDriver);courier.Actualizar("Nombre posterior a la visita",false);await owner.SaveChangesAsync();await publication.CommitAsync();
        }
        await using(var worker=s.Worker.CreateDbContext())await worker.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.request_management_rebuild({op})");
        Assert.Equal(8,await s.ReconcileAsync());
        var historical=(await client.GetFromJsonAsync<DetalleGestion>(root+$"/reportes/operaciones/{s.PartialShipment}"))!;
        Assert.Equal(detail.Operacion.CompromisoHastaUtc,historical.Operacion.CompromisoHastaUtc);
        Assert.Equal(detail.Bultos.Select(x=>(x.BultoId,x.RepartidorId,x.RutaId,x.EntregadoEnUtc)),historical.Bultos.Select(x=>(x.BultoId,x.RepartidorId,x.RutaId,x.EntregadoEnUtc)));
        Assert.Equal(661.30m,(await client.GetFromJsonAsync<CuentaGestion>(root+"/cuenta?"+Period+$"&comercioId={commerce}"))!.SaldoOperativoUyu);
        var cmd=new PrepararLiquidacion(Guid.NewGuid(),commerce,DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        async Task<LiquidacionDetalle> Prepare(PrepararLiquidacion c){using var response=await client.PostAsJsonAsync(root+"/liquidaciones",c);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());return (await response.Content.ReadFromJsonAsync<LiquidacionDetalle>())!;}
        var one=await Prepare(cmd);Assert.Equal(330.70m,one.Documento.TotalUyu);Assert.Equal(4,one.Lineas.Count);Assert.Equal(one.Documento.Id,(await Prepare(cmd)).Documento.Id);
        var two=await Prepare(cmd with{Id=Guid.NewGuid()});
        // Two distinct reviewed drafts compete for the same charges. Exactly one can claim them.
        var confirmations=await Task.WhenAll(client.PostAsync(root+$"/liquidaciones/{one.Documento.Id}/confirmar",null),client.PostAsync(root+$"/liquidaciones/{two.Documento.Id}/confirmar",null));
        Assert.Single(confirmations,x=>x.StatusCode==HttpStatusCode.OK);Assert.Single(confirmations,x=>x.StatusCode==HttpStatusCode.Conflict);
        var winner=(await confirmations.Single(x=>x.StatusCode==HttpStatusCode.OK).Content.ReadFromJsonAsync<LiquidacionDetalle>())!;
        var frozen=await client.GetStringAsync(root+$"/liquidaciones/{winner.Documento.Id}");
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsync(root+$"/liquidaciones/{winner.Documento.Id}/confirmar",null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(root+"/liquidaciones",cmd with{Id=Guid.NewGuid()})).StatusCode);
        var correction=new CorregirCuenta(Guid.NewGuid(),winner.Lineas[0].MovimientoId,-10.25m,"Corrección acordada por diferencia sintética.");
        foreach(var retry in Enumerable.Range(0,2)){using var response=await client.PostAsJsonAsync(root+"/correcciones",correction);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());}
        var corrected=(await client.GetFromJsonAsync<CuentaGestion>(root+"/cuenta?"+Period+$"&comercioId={commerce}"))!;Assert.Equal(651.05m,corrected.SaldoOperativoUyu);Assert.Equal(-10.25m,corrected.PendienteLiquidarUyu);
        Assert.Equal(frozen,await client.GetStringAsync(root+$"/liquidaciones/{winner.Documento.Id}"));
        Assert.Equal(new[]{"Borrador","Confirmada"},winner.Transiciones.Select(x=>x.Estado));
        await client.SetBearerForAccessAsync(s.Data,s.CommerceA.Id);
        var mine=(await client.GetFromJsonAsync<CuentaGestion>(root+"/cuenta?"+Period))!;Assert.Equal(651.05m,mine.SaldoOperativoUyu);Assert.Single(mine.Liquidaciones);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(root+$"/liquidaciones/{winner.Documento.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsync(root+$"/liquidaciones/{winner.Documento.Id}/confirmar",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(root+"/reportes?"+Period)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(root+"/cuenta?"+Period+$"&comercioId={s.CommerceB.ComercioId}")).StatusCode);
        var otherOperator=(await client.GetFromJsonAsync<CuentaGestion>($"api/gestion/operadores/{s.OperatorB.OperadorId}/cuenta?"+Period))!;Assert.Equal(220.40m,otherOperator.SaldoOperativoUyu);Assert.Empty(otherOperator.Liquidaciones);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"api/gestion/operadores/{s.OperatorB.OperadorId}/liquidaciones/{winner.Documento.Id}")).StatusCode);
        await client.SetBearerForAccessAsync(s.Data,s.CommerceB.Id);Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(root+$"/liquidaciones/{winner.Documento.Id}")).StatusCode);
        await client.SetBearerForAccessAsync(s.Data,s.OperatorB.Id);Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(root+"/reportes?"+Period)).StatusCode);
        NexoRuta.Domain.Administracion.AccesoUsuario readOnly;
        await using(var owner=s.Data.CreateDbContext())
        {
            var user=new NexoRuta.Domain.Administracion.Usuario("gestion-readonly@example.test",AuthenticationTestData.Hash("gestion-readonly@example.test"));
            var role=new NexoRuta.Domain.Administracion.Rol("consulta_gestion","Consulta de gestión",op,null);
            readOnly=new(user.Id,op,null,role);owner.AddRange(user,role,readOnly);
            var permission=await owner.Permisos.Where(x=>x.Codigo=="envios.consultar").Select(x=>x.Id).SingleAsync();owner.Add(new NexoRuta.Domain.Administracion.RolPermiso(role.Id,permission));await owner.SaveChangesAsync();
        }
        await client.SetBearerForAccessAsync(s.Data,readOnly.Id);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(root+"/reportes?"+Period)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(root+"/liquidaciones",cmd with{Id=Guid.NewGuid()})).StatusCode);
        await using(var readonlyDb=s.Runtime.CreateDbContext())
        {
            await using var scope=await readonlyDb.Database.BeginTransactionAsync();
            await readonlyDb.ApplyAsync(new(RlsActorKind.Operador,readOnly.UsuarioId,readOnly.Id,op));
            var denial=await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>readonlyDb.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.transition_settlement({winner.Documento.Id},{true})"));
            Assert.Equal("42501",denial.SqlState);
        }
        // Forced RLS and grants protect direct database access too.
        await using var restricted=s.Runtime.CreateDbContext();await using var tx=await restricted.Database.BeginTransactionAsync();
        await restricted.ApplyAsync(new(NexoRuta.Infrastructure.Persistence.RlsActorKind.Comercio,s.CommerceB.UsuarioId,s.CommerceB.Id,s.CommerceB.ComercioId));await restricted.SelectOperatorAsync(op);
        Assert.Equal(0,await restricted.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"Liquidaciones\" WHERE \"Id\"={winner.Documento.Id}").SingleAsync());
        Assert.False(await restricted.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'public.\"Liquidaciones\"','UPDATE') AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task OpcionesAutorizadasBuscablesPaginadasIncluyenInactivosYSinMezclarComercios()
    {
        var s=await GestionScenario.CreateAsync(postgres);
        using var factory=new IntegrationApiFactory(s.Runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings());using var client=factory.CreateHttpClient();
        var op=s.OperatorA.OperadorId!.Value;var root=$"api/gestion/operadores/{op}/opciones/";
        await client.SetBearerForAccessAsync(s.Data,s.OperatorA.Id);
        var commerce=(await client.GetFromJsonAsync<OpcionesGestion>(root+"Comercio"))!;
        Assert.Contains(commerce.Opciones,o=>o.Id==s.CommerceA.ComercioId && o.Nombre.Contains("Comercio"));
        Assert.False(commerce.HayMas);
        Assert.Empty((await client.GetFromJsonAsync<OpcionesGestion>(root+"Comercio?buscar=inexistente"))!.Opciones);
        await using(var db=s.Data.CreateDbContext()){
            var zone=await db.ZonasCobertura.FirstAsync(x=>x.OperadorId==op);
            db.Add(NexoRuta.Domain.ZonasCobertura.ZonaCobertura.Crear(op,zone.Nombre,zone.Geometria.Copy(),ahoraUtc:zone.CreadaUtc.AddSeconds(1)));
            var driver=await db.Repartidores.FirstAsync(x=>x.OperadorId==op);driver.Actualizar("Autor inactivo de prueba",false);
            for(var n=0;n<55;n++){
                var user=new NexoRuta.Domain.Administracion.Usuario($"opcion-{n}@example.test",AuthenticationTestData.Hash($"opcion-{n}@example.test"));
                var access=new NexoRuta.Domain.Administracion.AccesoUsuario(user.Id,op,null);db.AddRange(user,access);await db.SaveChangesAsync();
                db.Add(new NexoRuta.Domain.Reparto.Repartidor(access.Id,op,n<2?"Opción repetida":"Opción "+n.ToString("D2")));
            }
            await db.SaveChangesAsync();
        }
        var zones=(await client.GetFromJsonAsync<OpcionesGestion>(root+"Zona"))!;
        Assert.Equal(2,zones.Opciones.Count);Assert.Equal(2,zones.Opciones.Select(x=>x.Nombre).Distinct().Count());
        Assert.All(zones.Opciones,x=>Assert.Contains("Creada",x.Nombre));
        var first=(await client.GetFromJsonAsync<OpcionesGestion>(root+"Repartidor?buscar=Opción"))!;
        Assert.Equal(50,first.Opciones.Count);Assert.True(first.HayMas);
        var second=(await client.GetFromJsonAsync<OpcionesGestion>(root+"Repartidor?buscar=Opción&pagina=2"))!;
        Assert.Equal(5,second.Opciones.Count);Assert.False(second.HayMas);
        Assert.Empty(first.Opciones.Select(x=>x.Id).Intersect(second.Opciones.Select(x=>x.Id)));
        var duplicates=(await client.GetFromJsonAsync<OpcionesGestion>(root+"Repartidor?buscar=repetida"))!;
        Assert.Equal(2,duplicates.Opciones.Select(x=>x.Nombre).Distinct().Count());
        Assert.All(duplicates.Opciones,x=>Assert.Contains("@example.test",x.Nombre));
        var drivers=(await client.GetFromJsonAsync<OpcionesGestion>(root+"Repartidor?buscar=Autor"))!;
        Assert.Contains(drivers.Opciones,o=>o.Nombre.Contains("Inactivo"));
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(root+"Zona?pagina=0")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"api/gestion/operadores/{s.OperatorB.OperadorId}/opciones/Comercio")).StatusCode);
        await client.SetBearerForAccessAsync(s.Data,s.CommerceA.Id);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(root+"Comercio")).StatusCode);
        var own=(await client.GetFromJsonAsync<OpcionesGestion>(root+"Envio"))!;
        Assert.NotEmpty(own.Opciones);
        await client.SetBearerForAccessAsync(s.Data,s.CommerceB.Id);
        var other=(await client.GetFromJsonAsync<OpcionesGestion>(root+"Envio?seleccionado="+s.PartialShipment))!;
        Assert.Empty(other.Opciones);
    }
    [Fact]
    public async Task PrecisionDeFechaCanonicaExplicaAtribucionAusenteSinReescribirHistoria()
    {
        var data=await postgres.CreateDatabaseAsync();await using var db=data.CreateDbContext();
        var canonical=new DateTimeOffset(2026,11,7,23,19,46,TimeSpan.Zero).AddTicks(7);
        // Npgsql sends timestamptz at microsecond precision; PostgreSQL's text parser rounds the JSON value.
        var same=await db.Database.SqlQuery<bool>($"SELECT {canonical}::timestamptz = {canonical.ToString("O")}::timestamptz AS \"Value\"").SingleAsync();
        Assert.False(same);
        // Construct original immutable facts with sub-microsecond time; never update the journal.
        var scenario=await GestionScenario.CreateAsync(postgres,precisionSubMicrosegundo:true);
        await using var owned=scenario.Data.CreateDbContext();
        var shipment=scenario.PartialShipment;
        var physical=GestionScenario.Admission.AddHours(2).AddTicks(7);
        await scenario.ReconcileAsync();
        Assert.True(await owned.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM public."GestionBultos" WHERE "EnvioId"={shipment} AND "EntregadoEnUtc"={physical} AND "RepartidorId" IS NULL AND "RutaId" IS NULL) AS "Value"
            """).SingleAsync());
    }
}
