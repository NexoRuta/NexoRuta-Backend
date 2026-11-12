using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Reparto;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;
namespace NexoRuta.IntegrationTests.Gestion;

// Explicit synthetic accepted facts; never runs in production seed and never takes an external connection.
internal sealed class GestionScenario
{
    internal required IntegrationDatabase Data {get;init;}
    internal required IntegrationDatabase Runtime {get;init;}
    internal required IntegrationDatabase Worker {get;init;}
    internal required AccesoUsuario OperatorA {get;init;}
    internal required AccesoUsuario OperatorB {get;init;}
    internal required AccesoUsuario CommerceA {get;init;}
    internal required AccesoUsuario CommerceB {get;init;}
    internal required Guid SecondDriver {get;init;}
    internal required Guid PartialShipment {get;init;}
    internal required Guid CompleteShipment {get;init;}
    internal required Guid MissingCommitment {get;init;}
    internal static readonly DateTimeOffset Admission=new(2026,11,2,2,30,0,TimeSpan.Zero); // Nov 1, 23:30 in Montevideo.
    internal static readonly JsonSerializerOptions Json=new(){Converters={new JsonStringEnumConverter()}};

    internal static async Task<GestionScenario> CreateAsync(PostgresFixture postgres, bool precisionSubMicrosegundo=false)
    {
        var data=await postgres.CreateDatabaseAsync();await using var db=data.CreateDbContext();
        await AuthenticationTestData.Seeder(db,new AccesoInicial("gestion-a@example.test","Operador A sintético","Comercio A sintético","gestion-op-a@example.test")).SeedAsync();
        var a=await db.AccesosUsuario.SingleAsync(x=>x.OperadorId!=null);var c=await db.AccesosUsuario.SingleAsync(x=>x.ComercioId!=null);
        await AuthenticationTestData.Seeder(db,new AccesoInicial("gestion-b@example.test","Operador B sintético","Comercio B sintético","gestion-op-b@example.test")).SeedAsync();
        var b=await db.AccesosUsuario.SingleAsync(x=>x.OperadorId!=null && x.Id!=a.Id);var d=await db.AccesosUsuario.SingleAsync(x=>x.ComercioId!=null && x.Id!=c.Id);
        await ReglasEnvioTestData.SembrarInicialesAsync(db);
        await ServiciosEnvioTestData.PublicarServicioAsync(db,a.OperadorId!.Value,a.UsuarioId);
        await ServiciosEnvioTestData.PublicarServicioAsync(db,b.OperadorId!.Value,b.UsuarioId,200,20,400);
        var role=await db.Roles.SingleAsync(x=>x.Id==a.RolId);
        var driverUser=new Usuario("gestion-driver-2@example.test",AuthenticationTestData.Hash("gestion-driver-2@example.test"));
        var second=new AccesoUsuario(driverUser.Id,a.OperadorId,null,role);db.AddRange(driverUser,second);await db.SaveChangesAsync();
        foreach(var actor in new[]{a,b,second})db.Add(new Repartidor(actor.Id,actor.OperadorId!.Value,"Repartidor sintético "+actor.Id));
        foreach(var actor in new[]{a,b})db.Add(new ConfiguracionReparto(actor.OperadorId!.Value,1,"America/Montevideo",50,false,false));
        await db.SaveChangesAsync();
        // Add a pickup tariff as another published version; earlier delivery amounts retain their original version.
        async Task PublishPickup(AccesoUsuario actor)
        {
            await using var publication=await db.Database.BeginTransactionAsync();
            var root=await db.TarifariosOperador.SingleAsync(x=>x.OperadorId==actor.OperadorId);
            var v=VersionTarifario.CrearBorrador(actor.OperadorId!.Value,actor.UsuarioId);db.Add(v);await db.SaveChangesAsync();
            v.ReemplazarConfiguracion(0,[new(actor.OperadorId.Value,ModalidadTarifaria.Estandar,100,10,200,[]),new(actor.OperadorId.Value,ModalidadTarifaria.Retiro,70,5,100,[])]);await db.SaveChangesAsync();
            v.Publicar(v.VersionConcurrencia,root.UltimoNumeroPublicacion+1,actor.UsuarioId,new HashSet<Guid>{actor.OperadorId.Value});await db.SaveChangesAsync();root.Publicar(v);await db.SaveChangesAsync();await publication.CommitAsync();
        }
        await PublishPickup(a);
        await using(var publication=await db.Database.BeginTransactionAsync())
        {
            var root=await db.ReglasOperativasOperadores.SingleAsync(x=>x.OperadorId==a.OperadorId);
            var policy=NexoRuta.Domain.ReglasOperativas.VersionReglasOperativas.CrearBorrador(a.OperadorId!.Value,a.UsuarioId);db.Add(policy);await db.SaveChangesAsync();
            policy.ReemplazarConfiguracion(0,new(3,1,NexoRuta.Domain.ReglasOperativas.PoliticaDevolucionAlAgotarIntentos.DecisionOperativa,2880,1440,60,[NexoRuta.Domain.ReglasOperativas.TipoPruebaEntrega.Fotografia,NexoRuta.Domain.ReglasOperativas.TipoPruebaEntrega.NombreReceptor],[new("AUSENTE","Ausente",true)]){PermiteEntregaParcial=true,PoliticaPendientesParcial=NexoRuta.Domain.ReglasOperativas.PoliticaPendientesParcial.NuevaVisitaConRecargo,RecargoNuevaVisitaUyu=25});await db.SaveChangesAsync();
            policy.Publicar(policy.VersionConcurrencia,root.UltimoNumeroPublicacion+1,a.UsuarioId);await db.SaveChangesAsync();root.Publicar(policy);await db.SaveChangesAsync();await publication.CommitAsync();
        }
        var routes=new Dictionary<Guid,HojaRuta>();
        async Task<Envio> Shipment(AccesoUsuario op,AccesoUsuario commerce,string outcome,int parcels=1,bool fee=true)
        {
            var id=op.OperadorId!.Value;var dest=new Destinatario(id,"Destino sintético","destino@example.test");var address=new Direccion(id,"Dirección sintética 123");
            var rules=(await db.ReglasOperativasOperadores.SingleAsync(x=>x.OperadorId==id)).VersionVigenteId!.Value;
            var e=new Envio(id,commerce.ComercioId!.Value,commerce.UsuarioId,dest.Id,address.Id,rules,Admission);
            if(outcome=="pickup")e.ConfigurarRetiro(Guid.NewGuid(),"Cambio sintético",Admission.AddHours(1),Admission.AddHours(4),"synthetic-hash");
            var boxes=Enumerable.Range(0,parcels).Select(n=>new Bulto(id,e.Id,"GESTION-"+e.Id+"-"+n,1000,10,10,10)).ToArray();
            db.AddRange(dest,address,e);db.AddRange(boxes);await db.SaveChangesAsync();
            if(fee)await Charge(e,boxes,outcome=="pickup"?ModalidadTarifaria.Retiro:ModalidadTarifaria.Estandar);
            if(outcome=="missing")return e;
            if(outcome=="cancel") {e.CambiarEstado(EstadoEnvio.Cancelado,e.Version);await db.SaveChangesAsync();return e;}
            if(!routes.TryGetValue(op.Id,out var route))
            {
                var vehicle=new Vehiculo(id,"G"+Guid.NewGuid().ToString("N")[..12],1000000,100000000);db.Add(vehicle);await db.SaveChangesAsync();
                route=new HojaRuta(id,op.Id,vehicle,new DateOnly(2026,11,2),1);db.Add(route);routes.Add(op.Id,route);
            }
            route.AgregarParada(e.Id,Admission.AddHours(2));await db.SaveChangesAsync();
            if(e.EsRetiro){e.CambiarEstado(EstadoEnvio.RetiroSolicitado,e.Version);await db.SaveChangesAsync();e.CambiarEstado(EstadoEnvio.RetiroAsignado,e.Version);await db.SaveChangesAsync();}
            else{foreach(var box in boxes)box.RecibirEnDeposito();e.CambiarEstado(EstadoEnvio.EnDeposito,e.Version);await db.SaveChangesAsync();e.CambiarEstado(EstadoEnvio.AsignadoRuta,e.Version);await db.SaveChangesAsync();foreach(var box in boxes)box.Cargar(op.Id,route.Id);e.CambiarEstado(EstadoEnvio.EnTransito,e.Version);await db.SaveChangesAsync();}

            if(outcome=="failed")
            {
                e.CambiarEstado(EstadoEnvio.NoEntregado,e.Version);await db.SaveChangesAsync();
                await Journal(op,route,e,"IntentoFallido",Admission.AddHours(2),new{motivo="AUSENTE"});return e;
            }
            if(outcome=="pickup")
            {
                foreach(var box in boxes)box.Retirar(op.Id,route.Id);e.CambiarEstado(EstadoEnvio.Retirado,e.Version);await db.SaveChangesAsync();
                await Journal(op,route,e,"RetirarBultos",Admission.AddHours(2),new{resultadosBultos=boxes.Select(x=>new{bultoId=x.Id,entregado=true})});
                foreach(var box in boxes){box.DeclararRendicion(op.Id,route.Id);box.RecibirEnDeposito();}e.CambiarEstado(EstadoEnvio.RetiroEnDeposito,e.Version);await db.SaveChangesAsync();
                foreach(var box in boxes)box.EntregarAlComercio();e.CambiarEstado(EstadoEnvio.EntregadoComercio,e.Version);await db.SaveChangesAsync();return e;
            }
            var time=Admission.AddHours(2).AddTicks(precisionSubMicrosegundo?7:0);
            boxes[0].Entregar(op.Id,route.Id);boxes[0].RegistrarResultado(outcome=="proof"?ResultadoBulto.EntregaPendienteValidacion:ResultadoBulto.Entregado,boxes[0].Version,entregadoEn:time,vence:DateTimeOffset.UtcNow.AddHours(1));
            if(parcels>1)boxes[1].RegistrarPendiente("AUSENTE",boxes[1].Version);
            e.ActualizarResultadoBultos(boxes,DateTimeOffset.UtcNow);await db.SaveChangesAsync();
            await Journal(op,route,e,"EntregaBultos",time,new{resultadosBultos=boxes.Select((x,n)=>new{bultoId=x.Id,entregado=n==0,motivo=n==0?(string?)null:"AUSENTE"})});
            if(parcels>1)
            {
                // A subsequent accepted visit performed by a different driver; only its persisted surcharge is added.

                route.Paradas.Single(x=>x.EnvioId==e.Id).Completar();
                boxes[1].DeclararRendicion(op.Id,route.Id);boxes[1].RecibirEnDeposito();await db.SaveChangesAsync();
                var otherVehicle=new Vehiculo(id,"S"+Guid.NewGuid().ToString("N")[..12],1000000,100000000);db.Add(otherVehicle);await db.SaveChangesAsync();
                var other=new HojaRuta(id,second.Id,otherVehicle,new DateOnly(2026,11,5),1);other.AgregarParada(e.Id,Admission.AddDays(3));db.Add(other);await db.SaveChangesAsync();
                other.Despachar(other.Version);await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.apply_partial_visit_fee({e.Id},{other.Id})");
                boxes[1].Cargar(second.Id,other.Id);boxes[1].Entregar(second.Id,other.Id);boxes[1].RegistrarResultado(ResultadoBulto.Entregado,boxes[1].Version,entregadoEn:Admission.AddDays(3));e.ActualizarResultadoBultos(boxes,DateTimeOffset.UtcNow);await db.SaveChangesAsync();
                await Journal(second,other,e,"EntregaBultos",Admission.AddDays(3),new{resultadosBultos=new[]{new{bultoId=boxes[1].Id,entregado=true}}});
            }
            return e;
        }
        async Task Charge(Envio e,Bulto[] boxes,ModalidadTarifaria mode)
        {
            var root=await db.TarifariosOperador.SingleAsync(x=>x.OperadorId==e.OperadorId);var v=await db.VersionesTarifario.SingleAsync(x=>x.Id==root.VersionVigenteId);var t=await db.TarifasZonaModalidad.SingleAsync(x=>x.VersionId==v.Id && x.Modalidad==mode);
            var amount=CalculadoraTarifas.Calcular(new(e.OperadorId,mode,t.ImporteBaseUyu,t.PrecioPorKilogramoUyu,t.PrecioPorMetroCubicoUyu,[],v.Id,v.NumeroVersionPublicada,v.VersionConcurrencia),boxes.Select(x=>new BultoCalculo(x.PesoGramos,x.LargoCentimetros,x.AnchoCentimetros,x.AltoCentimetros)).ToArray());
            var result=JsonSerializer.SerializeToNode(amount,Json)!;
            var service=JsonSerializer.SerializeToNode(ServiciosEnvioTestData.Comando(e.OperadorId),Json)!;service["Modalidad"]=mode.ToString();
            var zone=await db.ZonasCobertura.SingleAsync(x=>x.Id==e.OperadorId);var coverage=JsonSerializer.Serialize(new{ZonaId=zone.Id,VersionZona=zone.Version,RevisionZona=zone.RevisionActual});
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.\"TarifasEnvio\"(\"Id\",\"OperadorId\",\"EnvioId\",\"VersionTarifarioId\",\"Servicio\",\"Resultado\",\"Cobertura\") VALUES({Guid.CreateVersion7()},{e.OperadorId},{e.Id},{v.Id},{service.ToJsonString()}::jsonb,{result.ToJsonString()}::jsonb,{coverage}::jsonb)");
        }
        async Task Journal(AccesoUsuario actor,HojaRuta route,Envio e,string kind,DateTimeOffset time,object details)
        {
            var sequence=1+await db.OperacionesJornada.CountAsync(x=>x.RutaId==route.Id);
            var id=Guid.NewGuid();var payload=JsonSerializer.Serialize(new{id,secuencia=sequence,tipo=kind,envioId=e.Id,ocurridaEn=time,datos=details});
            db.Add(new OperacionJornada{Id=id,OperadorId=e.OperadorId,EnvioId=e.Id,RutaId=route.Id,AccesoId=actor.Id,Secuencia=sequence,Tipo=kind,Payload=payload,Hash=new string('a',64),Resultado="Aplicada",Respuesta="{}",RecibidaEnUtc=DateTimeOffset.UtcNow});await db.SaveChangesAsync();
        }
        var complete=await Shipment(a,c,"complete");var partial=await Shipment(a,c,"complete",2);
        await Shipment(a,c,"failed");await Shipment(a,c,"cancel");await Shipment(a,c,"pickup");var missing=await Shipment(a,c,"missing",fee:false);await Shipment(a,c,"proof");
        await Shipment(a,d,"complete");await Shipment(b,c,"complete");
        foreach(var route in routes.Values){route.Despachar(route.Version);route.RegistrarInicio(Admission.AddHours(1));route.ConfirmarSalida(Admission.AddHours(1));}await db.SaveChangesAsync();
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var password=Guid.NewGuid().ToString("N");var login="management_worker_"+Guid.NewGuid().ToString("N");
        await using(var connection=new NpgsqlConnection(data.ConnectionString)){await connection.OpenAsync();await using var create=new NpgsqlCommand($"CREATE ROLE {login} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT nexoruta_worker TO {login}",connection);await create.ExecuteNonQueryAsync();}
        var worker=new IntegrationDatabase(new NpgsqlConnectionStringBuilder(data.ConnectionString){Username=login,Password=password}.ConnectionString);
        return new(){Data=data,Runtime=runtime,Worker=worker,OperatorA=a,OperatorB=b,CommerceA=c,CommerceB=d,SecondDriver=second.Id,PartialShipment=partial.Id,CompleteShipment=complete.Id,MissingCommitment=missing.Id};
    }
    internal async Task<int> ReconcileAsync()
    {
        await using var db=Worker.CreateDbContext();
        return await db.Database.SqlQueryRaw<int>("SELECT nexoruta_app.reconcile_management(NULL,NULL,1000) AS \"Value\"").SingleAsync();
    }
}
