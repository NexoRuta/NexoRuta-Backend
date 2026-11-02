using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Estimaciones;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Reparto;
public sealed partial class EfRutasRepository
{
    private sealed record ContextoEstimacion(SolicitudEstimacionInicial Pedido,long PerfilVersion,long VehiculoVersion,string EstadoRuta,string Compatibilidad,ContextoRecorrido? Recorrido = null);
    private static string HashContexto(ContextoEstimacion c)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(c.Recorrido is null ? (object)new {c.Pedido,c.PerfilVersion,c.VehiculoVersion,c.EstadoRuta,c.Compatibilidad} : EntradasRecorrido(c))));
    public async Task<IReadOnlyList<PerfilVialDatos>> PerfilesVialesAsync(Guid op,CancellationToken ct)
    {
        await using var tx=await db.BeginOrJoinAsync(ct);await Contexto(op,ct);
        var vehiculos=await db.Vehiculos.AsNoTracking().Where(v=>v.OperadorId==op).ToArrayAsync(ct);var perfiles=await db.Set<PerfilVialRegistro>().AsNoTracking().Where(p=>p.OperadorId==op).ToArrayAsync(ct);
        var result=vehiculos.Select(v=>{var p=perfiles.SingleOrDefault(x=>x.VehiculoId==v.Id);return new PerfilVialDatos(v.Id,v.Matricula,p?.Version??0,p?.Compatibilidad??"SinDeterminar",p?.ActorId,p?.ConfirmadoEnUtc,p?.Motivo);}).ToArray();await tx.CommitAsync(ct);return result;
    }
    public async Task<PerfilVialDatos?> ConfigurarPerfilAsync(ContextoUsuario a,Guid vehiculo,ConfigurarPerfilVialDatos d,CancellationToken ct)
    {
        var op=a.OperadorId??throw new RepartoNoPermitidoException();
        if(d.Version<0 || d.Compatibilidad is not("SinDeterminar" or "DriveConfirmado" or "NoCompatible") || d.Compatibilidad=="DriveConfirmado"&&!d.ConfirmarCompatibilidad || string.IsNullOrWhiteSpace(d.Motivo)||d.Motivo.Length>500)throw new ArgumentException("Indicá compatibilidad específica con drive y motivo de hasta 500 caracteres; la capacidad no determina el perfil.");
        await using var tx=await db.BeginOrJoinAsync(ct);await Contexto(op,ct);await Bloquear(op,ct);
        var v=await db.Vehiculos.AsNoTracking().SingleOrDefaultAsync(x=>x.OperadorId==op&&x.Id==vehiculo,ct);if(v is null)return null;
        var p=await db.Set<PerfilVialRegistro>().SingleOrDefaultAsync(x=>x.OperadorId==op&&x.VehiculoId==vehiculo,ct);
        if((p?.Version??0)!=d.Version)throw new ConflictoRepartoException("El perfil cambió. Conservá los datos y revisá su versión; no se reintenta automáticamente.");
        if(p is null){p=new(){OperadorId=op,VehiculoId=vehiculo};db.Add(p);}p.Version++;p.Compatibilidad=d.Compatibilidad;p.Motivo=d.Motivo.Trim();p.ActorId=a.AccesoId;p.ConfirmadoEnUtc=reloj.GetUtcNow();
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(v.Id,v.Matricula,p.Version,p.Compatibilidad,p.ActorId,p.ConfirmadoEnUtc,p.Motivo);
    }
    private async Task<ContextoEstimacion?> ContextoCalculo(Guid op,Guid ruta,CancellationToken ct,bool recorrido = false)
    {
        var h=await db.HojasRuta.AsNoTracking().Include(h=>h.Paradas).SingleOrDefaultAsync(h=>h.OperadorId==op&&h.Id==ruta,ct);if(h is null)return null;
        var hoja=await Datos(h,ct);var t=hoja.PlanTemporal;
        var v=await db.Vehiculos.AsNoTracking().SingleAsync(v=>v.OperadorId==op&&v.Id==h.VehiculoId,ct);
        var perfil=await db.Set<PerfilVialRegistro>().AsNoTracking().SingleOrDefaultAsync(p=>p.OperadorId==op&&p.VehiculoId==v.Id,ct);
        var ids=hoja.Paradas.Select(p=>p.EnvioId).ToArray();var confirmados=await (from e in db.Envios where e.OperadorId==op&&ids.Contains(e.Id) join d in db.Direcciones on e.DireccionId equals d.Id where d.OperadorId==op&&d.ConfirmadaEnUtc!=null select e.Id).ToArrayAsync(ct);
        var solicitud=new SolicitudEstimacionInicial(op,ruta,h.Version,t?.BaseVersion,t?.LatitudBase is {} lat&&t.LongitudBase is {} lon?new(lat,lon):null,t?.BaseConfirmadaEnUtc!=null,t?.SalidaPlanificadaEnUtc,hoja.Politica.ZonaHoraria,perfil?.Compatibilidad=="DriveConfirmado"?"drive":null,t?.MargenRetornoMinutos,hoja.Paradas.Select(p=>new ParadaEstimable(p.EnvioId,p.Orden,p.Activa,new(p.Latitud,p.Longitud),confirmados.Contains(p.EnvioId),p.ServicioMinutos,p.LlegadaEstimadaUtc)).ToArray());
        var contexto=new ContextoEstimacion(solicitud,perfil?.Version??0,v.Version,h.Estado.ToString(),perfil?.Compatibilidad??"SinDeterminar");
        return recorrido ? await ConRecorrido(contexto,h,ct) : contexto;
    }
    public async Task<EstimacionRutaDatos?> EstimarAsync(ContextoUsuario a,Guid ruta,CalcularEstimacionDatos d,CancellationToken ct)
    {
        // The request middleware owns a read-only authorization transaction. Use the same repository with
        // a separate connection so its two short phases can commit/release locks before external I/O.
        await using var aislado=new NexoRutaDbContext((DbContextOptions<NexoRutaDbContext>)db.GetService<IDbContextOptions>());
        return await new EfRutasRepository(aislado,reloj,proveedorVial,opcionesRouting).EstimarConTransacciones(a,ruta,d,ct);
    }
    private async Task RevalidarCalculo(ContextoUsuario a,CancellationToken ct)
    {
        await db.ApplyAsync(RlsActorContext.From(a),ct);
        var actual=await new NexoRuta.Infrastructure.Administracion.Repositories.EfAccesosUsuarioRepository(db).ObtenerAsync(a.AccesoId,ct);
        if(actual is null||actual.VersionSesion!=a.VersionSesion||actual.UsuarioId!=a.UsuarioId||actual.OperadorId!=a.OperadorId||actual.ComercioId!=null||actual.Estado!=NexoRuta.Domain.Administracion.EstadoOrganizacion.Activa
            ||!await new NexoRuta.Infrastructure.Administracion.Repositories.EfPermisosUsuarioRepository(db).TienePermisoAsync(a.UsuarioId,a.AccesoId,"envios.gestionar",ct))throw new RepartoNoPermitidoException();
    }
    private async Task<EstimacionRutaDatos?> EstimarConTransacciones(ContextoUsuario a,Guid ruta,CalcularEstimacionDatos d,CancellationToken ct)
    {
        var op=a.OperadorId??throw new RepartoNoPermitidoException();if(d.ComandoId==Guid.Empty||d.VersionRuta<0)throw new ArgumentException("Indicá comando y versión de ruta válidos.");
        ContextoEstimacion inicial;
        await using(var tx=await db.BeginOrJoinAsync(ct))
        {
            await RevalidarCalculo(a,ct);await Contexto(op,ct);await Bloquear(op,ct);var c=await ContextoCalculo(op,ruta,ct,d.OcupacionId is not null);if(c is null)return null;
            var previo=await db.Set<EstimacionRegistro>().AsNoTracking().SingleOrDefaultAsync(x=>x.OperadorId==op&&x.ComandoId==d.ComandoId,ct);
            if(previo is not null){VerificarReintento(previo,a,ruta,d);await tx.CommitAsync(ct);return ResultadoEstimacion(previo,c);}
            if(c.Pedido.VersionRuta!=d.VersionRuta||d.OcupacionId is null && c.EstadoRuta is not("Planificada" or "Despachada"))throw new ConflictoRepartoException("La ruta cambió o ya inició ejecución. Solo se estima explícitamente una ruta planificada o despachada.");if(d.OcupacionId is not null && (d.OcupacionId==Guid.Empty || c.Recorrido?.OcupacionId!=d.OcupacionId))throw new ConflictoRepartoException("La ocupación solicitada no corresponde a la ejecución vigente.");
            inicial=c;await tx.CommitAsync(ct);
        }
        // External I/O runs outside the operator lock. All input is re-read before appending history.
        using var limite=CancellationTokenSource.CreateLinkedTokenSource(ct);limite.CancelAfter(TimeSpan.FromSeconds(30));
        EstimacionInicial resultado;
        try{resultado=inicial.Recorrido is {Motivo:not "Admitida"} rc
            ? new(rc.Motivo is "EjecucionNoDisponible" ? EstadoEstimacionInicial.EjecucionNoDisponible : EstadoEstimacionInicial.PosicionNoAdmisible,"",reloj.GetUtcNow(),null,inicial.Pedido.ZonaHoraria,[],[],null,null,null)
            : inicial.Pedido.Paradas.Count(p=>p.Activa)+1>(opcionesRouting??new()).MaxTramosCalculo
            ?new(EstadoEstimacionInicial.CuotaExcedida,"",reloj.GetUtcNow(),null,inicial.Pedido.ZonaHoraria,[],[],null,null,null)
            : inicial.Recorrido?.Posicion is {} gps
                ? await new EstimadorRutaInicial(proveedorVial,reloj).CalcularRecorridoAsync(inicial.Pedido,gps.Punto,limite.Token)
                : await new EstimadorRutaInicial(proveedorVial,reloj).CalcularAsync(inicial.Pedido,limite.Token);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){resultado=new(EstadoEstimacionInicial.TiempoAgotado,"",reloj.GetUtcNow(),null,inicial.Pedido.ZonaHoraria,[],[],null,null,null);}
        ct.ThrowIfCancellationRequested();
        await using var fin=await db.BeginOrJoinAsync(ct);await RevalidarCalculo(a,ct);await Contexto(op,ct);await Bloquear(op,ct);var actual=await ContextoCalculo(op,ruta,ct,d.OcupacionId is not null);
        var repetido=await db.Set<EstimacionRegistro>().AsNoTracking().SingleOrDefaultAsync(x=>x.OperadorId==op&&x.ComandoId==d.ComandoId,ct);
        if(repetido is not null){VerificarReintento(repetido,a,ruta,d);await fin.CommitAsync(ct);return ResultadoEstimacion(repetido,actual!);}
        if(actual is null||HashContexto(inicial)!=HashContexto(actual)||inicial.Recorrido is {} rcAnterior && rcAnterior.UltimoIntento!=actual.Recorrido?.UltimoIntento)throw new ConflictoRepartoException("La ruta, el vehículo o su perfil cambiaron durante el cálculo. Se descartó la respuesta obsoleta; no se aplicó ningún horario.");
        var numero=(await db.Set<EstimacionRegistro>().Where(x=>x.OperadorId==op&&x.RutaId==ruta).MaxAsync(x=>(long?)x.NumeroVersion,ct)??0)+1;
        var registro=new EstimacionRegistro{OperadorId=op,RutaId=ruta,ComandoId=d.ComandoId,NumeroVersion=numero,VersionRuta=d.VersionRuta,ActorId=a.AccesoId,RegistradaEnUtc=reloj.GetUtcNow(),ContextoHash=HashContexto(inicial),Entrada=JsonSerializer.Serialize(inicial),Resultado=JsonSerializer.Serialize(resultado),Motivo=inicial.Recorrido is {Motivo:not "Admitida"} motivoGps ? motivoGps.Motivo : inicial.Compatibilidad=="SinDeterminar"?"PerfilPendiente":inicial.Compatibilidad=="NoCompatible"?"PerfilNoCompatible":resultado.Estado.ToString()};
        db.Add(registro);await db.SaveChangesAsync(ct);await fin.CommitAsync(ct);return ResultadoEstimacion(registro,actual);
    }
    private static void VerificarReintento(EstimacionRegistro r,ContextoUsuario a,Guid ruta,CalcularEstimacionDatos d)
    {if(r.ActorId!=a.AccesoId||r.RutaId!=ruta||r.VersionRuta!=d.VersionRuta||JsonSerializer.Deserialize<ContextoEstimacion>(r.Entrada)?.Recorrido?.OcupacionId!=d.OcupacionId)throw new ConflictoRepartoException("Ese comando ya se usó con otra solicitud o actor.");}
    private EstimacionRutaDatos ResultadoEstimacion(EstimacionRegistro r,ContextoEstimacion c)
    {var entrada=JsonSerializer.Deserialize<ContextoEstimacion>(r.Entrada)!;var resultado=JsonSerializer.Deserialize<EstimacionInicial>(r.Resultado)!;
        if(entrada.Recorrido is {} recorrido){var vigente=c.Recorrido is not null && r.ContextoHash==HashContexto(c) && c.Recorrido.Motivo=="Admitida" && r.NumeroVersion>=c.Recorrido.UltimoExito;
            var vigenciaRecorrido=!vigente?VigenciaEstimacion.Invalidada:resultado.ValidoHastaUtc is {} hasta?(reloj.GetUtcNow()>=hasta?VigenciaEstimacion.Vencida:VigenciaEstimacion.Vigente):VigenciaEstimacion.Desconocida;
            return new(r.RutaId,r.ComandoId,r.NumeroVersion,r.VersionRuta,r.ActorId,r.RegistradaEnUtc,resultado,r.Motivo,vigenciaRecorrido.ToString(),"DuranteRecorrido",recorrido.OcupacionId,recorrido.Posicion,recorrido.CriteriosGps,false,
                vigente?null:c.Recorrido?.Motivo is not null and not "Admitida"?c.Recorrido.Motivo:r.NumeroVersion<c.Recorrido?.UltimoExito?"ActualizacionPosterior":"EntradasCambiadas");}
        var vigencia=r.ContextoHash!=HashContexto(c)?VigenciaEstimacion.Invalidada:EstimadorRutaInicial.ObtenerVigencia(resultado,c.Pedido,reloj.GetUtcNow());return new(r.RutaId,r.ComandoId,r.NumeroVersion,r.VersionRuta,r.ActorId,r.RegistradaEnUtc,resultado,r.Motivo,vigencia.ToString());}
    public async Task<IReadOnlyList<EstimacionRutaDatos>?> EstimacionesAsync(Guid op,Guid ruta,long? antes,CancellationToken ct)
    {
        await using var tx=await db.BeginOrJoinAsync(ct);await Contexto(op,ct);var actual=await ContextoCalculo(op,ruta,ct);if(actual is null)return null;
        var registros=await db.Set<EstimacionRegistro>().AsNoTracking().Where(x=>x.OperadorId==op&&x.RutaId==ruta&&(antes==null||x.NumeroVersion<antes)).OrderByDescending(x=>x.NumeroVersion).Take(20).ToArrayAsync(ct);
        // Keep the last successful initial/update visible even after more than a page of failures.
        var ultimosExitos=new List<EstimacionRegistro>();var tiposExito=new HashSet<bool>();
        if(antes is null)
            await foreach(var exito in db.Set<EstimacionRegistro>().AsNoTracking().Where(x=>x.OperadorId==op&&x.RutaId==ruta&&x.Motivo=="Calculada").OrderByDescending(x=>x.NumeroVersion).AsAsyncEnumerable().WithCancellation(ct))
            {if(tiposExito.Add(JsonSerializer.Deserialize<ContextoEstimacion>(exito.Entrada)!.Recorrido is null))ultimosExitos.Add(exito);if(tiposExito.Count==2)break;}
        var todos=registros.Concat(ultimosExitos).DistinctBy(x=>x.NumeroVersion).OrderByDescending(x=>x.NumeroVersion).ToArray();
        var recorridoActual=todos.Any(r=>JsonSerializer.Deserialize<ContextoEstimacion>(r.Entrada)!.Recorrido is not null)?await ContextoCalculo(op,ruta,ct,true):null;
        var result=todos.Select(r=>ResultadoEstimacion(r,JsonSerializer.Deserialize<ContextoEstimacion>(r.Entrada)!.Recorrido is null?actual:recorridoActual!) with{EsReferencia=!registros.Any(x=>x.NumeroVersion==r.NumeroVersion)}).ToArray();await tx.CommitAsync(ct);return result;
    }
    private async Task<(string Estado,string Motivo)> EstadoTemporalActual(NexoRuta.Domain.Reparto.HojaRuta h,CancellationToken ct)
    {
        var ultimo=await db.Set<EstimacionRegistro>().AsNoTracking().Where(x=>x.OperadorId==h.OperadorId&&x.RutaId==h.Id).OrderByDescending(x=>x.NumeroVersion).FirstOrDefaultAsync(ct);
        if(ultimo is null)return("SinEstimacion",MotivoTemporal(h));
        if(JsonSerializer.Deserialize<ContextoEstimacion>(ultimo.Entrada)?.Recorrido is not null)
            return ("ActualizacionRecorrido", "Consultar posición fuente, antigüedad y vigencia en el histórico vial separado.");
        var perfil=await db.Set<PerfilVialRegistro>().AsNoTracking().SingleOrDefaultAsync(x=>x.OperadorId==h.OperadorId&&x.VehiculoId==h.VehiculoId,ct);
        var versionVehiculo=await db.Vehiculos.Where(x=>x.OperadorId==h.OperadorId&&x.Id==h.VehiculoId).Select(x=>x.Version).SingleAsync(ct);
        using var entrada=JsonDocument.Parse(ultimo.Entrada);
        if(ultimo.VersionRuta!=h.Version||entrada.RootElement.GetProperty("PerfilVersion").GetInt64()!=(perfil?.Version??0)||entrada.RootElement.GetProperty("VehiculoVersion").GetInt64()!=versionVehiculo)return("Desactualizada","Las entradas cambiaron; consultar histórico separado.");
        return(JsonSerializer.Deserialize<EstimacionInicial>(ultimo.Resultado)!.Estado.ToString(),ultimo.Motivo);
    }
}
