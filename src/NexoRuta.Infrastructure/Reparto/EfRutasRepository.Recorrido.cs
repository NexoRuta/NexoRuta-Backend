using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Estimaciones;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Infrastructure.Reparto;
public sealed partial class EfRutasRepository
{
    private sealed record ContextoRecorrido(Guid? OcupacionId, PosicionProyeccion? Posicion, string Motivo,
        long UltimoIntento, long UltimoExito, AdmisionGpsOptions CriteriosGps,
        Guid RepartidorAccesoId,Guid JornadaId,Guid VehiculoId,DateTimeOffset? SalidaEnUtc,DateTimeOffset? AdquiridaEnUtc);

    // Journey sequence/version also advances for evidence, conflicts and out-of-order GPS receipt.
    // Only actual itinerary inputs enter this fingerprint, not those unrelated increments or current wall time.
    private static object EntradasRecorrido(ContextoEstimacion c) => new {
        c.Pedido.OperadorId,c.Pedido.RutaId,c.Pedido.VersionBase,c.Pedido.Base,c.Pedido.BaseConfirmada,
        c.Pedido.ZonaHoraria,c.Pedido.PerfilVial,c.Pedido.MargenOperativoMinutos,c.Pedido.Paradas,
        c.PerfilVersion,c.Compatibilidad,c.EstadoRuta,c.Recorrido!.OcupacionId,c.Recorrido.Posicion,c.Recorrido.Motivo,c.Recorrido.CriteriosGps,
        c.Recorrido.RepartidorAccesoId,c.Recorrido.JornadaId,c.Recorrido.VehiculoId,c.Recorrido.SalidaEnUtc,c.Recorrido.AdquiridaEnUtc
    };

    private async Task<ContextoEstimacion> ConRecorrido(ContextoEstimacion c,HojaRuta h,CancellationToken ct)
    {
        var ahora=reloj.GetUtcNow();
        var o=await db.OcupacionesRuta.AsNoTracking().SingleOrDefaultAsync(o=>o.OperadorId==h.OperadorId&&o.RutaId==h.Id
            &&o.VehiculoId==h.VehiculoId&&o.RepartidorAccesoId==h.RepartidorAccesoId&&o.LiberadaEnUtc==null&&o.AnuladaEnUtc==null,ct);
        var motivo="EjecucionNoDisponible";PosicionProyeccion? posicion=null;
        if(o is {RetornoEnUtc:null,AdquiridaEnUtc:not null} && h.InicioEnUtc is not null && h.SalidaEnUtc is not null
            && h.Estado is EstadoHojaRuta.EnCurso or EstadoHojaRuta.Finalizada)
        {
            var inicio=new[]{o.AdquiridaEnUtc.Value,h.InicioEnUtc.Value,h.SalidaEnUtc.Value}.Max();
            DateTimeOffset? minimaProyeccion=null;
            await foreach(var anterior in db.Set<EstimacionRegistro>().AsNoTracking().Where(x=>x.OperadorId==h.OperadorId&&x.RutaId==h.Id&&x.Motivo=="Calculada").OrderByDescending(x=>x.NumeroVersion).AsAsyncEnumerable().WithCancellation(ct))
                if(JsonSerializer.Deserialize<ContextoEstimacion>(anterior.Entrada)?.Recorrido is {} previo&&previo.OcupacionId==o.Id&&previo.Posicion is {} usada){minimaProyeccion=usada.MedidaEnUtc;break;}
            // A tighter admission policy may reject the previous source, but never authorizes rewinding it.
            PosicionProyeccion? mejor=null;PosicionProyeccion? rechazada=null;bool invalida=false;
            // ponytail: O(n) scan of this execution log, constant memory. If GPS volume warrants it, index measured facts in the existing journal rather than adding another tracking stream.
            var operaciones=db.OperacionesJornada.AsNoTracking().Where(x=>x.OperadorId==h.OperadorId&&x.RutaId==h.Id
                &&x.AccesoId==h.RepartidorAccesoId&&x.Tipo=="Posicion"&&x.Resultado=="Aplicada"
                &&x.RecibidaEnUtc>=o.AdquiridaEnUtc&&x.RecibidaEnUtc<=ahora).AsAsyncEnumerable();
            await foreach(var operacion in operaciones.WithCancellation(ct))
            {
                PosicionProyeccion p;
                try {
                    var datos=JsonSerializer.Deserialize<NexoRuta.Application.Reparto.Dtos.OperacionRepartoDatos>(operacion.Payload,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Datos;
                    var d=datos.Deserialize<DatosOperacionReparto>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if(d?.Latitud is not {} lat || d.Longitud is not {} lon || d.PrecisionMetros is not {} precision || d.PosicionEnUtc is not {} medida){invalida=true;continue;}
                    p=new(operacion.Id,new(lat,lon),medida.ToUniversalTime(),operacion.RecibidaEnUtc,precision);
                }catch(Exception e)when(e is JsonException or InvalidOperationException or NullReferenceException){invalida=true;continue;}
                var seleccion=SelectorPosicion.Elegir(mejor is null?new[]{p}:new[]{mejor,p},ahora,inicio,(opcionesRouting??new()).Gps,minimaProyeccion);
                if(seleccion.Posicion is {} admisible)mejor=admisible;
                if(rechazada is null || p.MedidaEnUtc>rechazada.MedidaEnUtc || p.MedidaEnUtc==rechazada.MedidaEnUtc&&p.Id.CompareTo(rechazada.Id)<0)rechazada=p;
            }
            var final=SelectorPosicion.Elegir(mejor is not null?new[]{mejor}:rechazada is not null?new[]{rechazada}:[],ahora,inicio,(opcionesRouting??new()).Gps,minimaProyeccion);
            posicion=final.Posicion;motivo=final.Motivo=="SinPosicion"&&invalida?"GpsInvalido":final.Motivo;
        }
        // An unsuccessful delivery is not an automatic retry: the current execution authorizes only EnTransito.
        // Delivered, pending evidence, failed/return/cancelled shipments and suspended stops are not future visits.
        var ids=c.Pedido.Paradas.Select(x=>x.EnvioId).ToArray();
        var pendientes=await db.Envios.AsNoTracking().Where(e=>e.OperadorId==h.OperadorId&&ids.Contains(e.Id)
            &&e.Estado==EstadoEnvio.EnTransito&&e.EntregaFisicaEnUtc==null).Select(e=>e.Id).ToArrayAsync(ct);
        var pedido=c.Pedido with {SalidaPlanificadaEnUtc=ahora,Paradas=c.Pedido.Paradas.Where(p=>p.Activa&&pendientes.Contains(p.EnvioId)).OrderBy(p=>p.Orden).ToArray()};
        var ultimo=await db.Set<EstimacionRegistro>().Where(x=>x.OperadorId==h.OperadorId&&x.RutaId==h.Id).MaxAsync(x=>(long?)x.NumeroVersion,ct)??0;
        var exito=await db.Set<EstimacionRegistro>().Where(x=>x.OperadorId==h.OperadorId&&x.RutaId==h.Id&&x.Motivo=="Calculada").MaxAsync(x=>(long?)x.NumeroVersion,ct)??0;
        return c with {Pedido=pedido,Recorrido=new(o?.Id,posicion,motivo,ultimo,exito,(opcionesRouting??new()).Gps,h.RepartidorAccesoId,h.JornadaId,h.VehiculoId,h.SalidaEnUtc,o?.AdquiridaEnUtc)};
    }
}
