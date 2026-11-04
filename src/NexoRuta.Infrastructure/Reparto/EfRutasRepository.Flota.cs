using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfRutasRepository
{
    private static readonly JsonSerializerOptions JsonFlota = new(JsonSerializerDefaults.Web);
    public async Task<IReadOnlyList<VehiculoFlotaDatos>> FlotaAsync(Guid operadorId, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct); await Bloquear(operadorId, ct);
        var vehiculos = await db.Vehiculos.AsNoTracking().Where(v => v.OperadorId == operadorId).OrderBy(v => v.Matricula).ToArrayAsync(ct);
        var ocupaciones = await db.OcupacionesRuta.AsNoTracking().Where(o => o.OperadorId == operadorId).ToArrayAsync(ct);
        var rutasIds = ocupaciones.Select(o => o.RutaId).ToArray();
        var rutas = await db.HojasRuta.AsNoTracking().Where(h => h.OperadorId == operadorId && rutasIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var pendientes = await CargaPendiente(operadorId).ToArrayAsync(ct);
        var rutasVehiculo = await db.HojasRuta.AsNoTracking().Where(h => h.OperadorId == operadorId)
            .Select(h => new { h.Id, h.VehiculoId, h.Estado, h.Fecha }).ToDictionaryAsync(h => h.Id, ct);
        var enviosPendientes = pendientes.Select(b => b.EnvioId).Distinct().ToArray();
        var versiones = await db.Envios.AsNoTracking().Where(e => e.OperadorId == operadorId && enviosPendientes.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Version, ct);
        var hechosCarga = await db.OperacionesJornada.AsNoTracking().Where(o => o.OperadorId == operadorId && o.Resultado == "Aplicada"
            && (o.Tipo == "InicioJornada" || o.Tipo == "CargarBulto" || o.Tipo == "ConfirmarCarga")).Select(o => o.RutaId).Distinct().ToArrayAsync(ct);
        var salida = vehiculos.Select(v => new VehiculoFlotaDatos(v.Id, v.Matricula, v.Activo, v.Version, v.VerificacionInicialEnUtc,
            pendientes.Count(b => b.RutaCustodiaId is { } r && rutasVehiculo.GetValueOrDefault(r)?.VehiculoId == v.Id),
            ocupaciones.Where(o => o.VehiculoId == v.Id && o.Activa).Select(o =>
            {
                var h = rutas[o.RutaId];
                return new OcupacionFlotaDatos(o.Id, o.RutaId, o.RepartidorAccesoId, h.Fecha, h.Estado.ToString(), o.Version,
                    o.Legada, o.AdquiridaEnUtc, o.RetornoEnUtc, pendientes.Count(b => b.RutaCustodiaId == h.Id),
                    h.Estado == EstadoHojaRuta.Despachada && h.InicioEnUtc is null && h.SalidaEnUtc is null && !hechosCarga.Contains(h.Id));
            }).ToArray(), ocupaciones.Where(o => o.VehiculoId == v.Id).Max(o => o.LiberadaEnUtc),
            pendientes.Where(b => b.RutaCustodiaId is { } r && rutasVehiculo.GetValueOrDefault(r)?.VehiculoId == v.Id
                && rutasVehiculo[r].Estado == EstadoHojaRuta.Finalizada && !ocupaciones.Any(o => o.RutaId == r))
                .GroupBy(b => new { b.EnvioId, Ruta = b.RutaCustodiaId!.Value }).Select(g =>
                    new CargaInicialFlotaDatos(g.Key.EnvioId, versiones[g.Key.EnvioId], g.Key.Ruta, rutasVehiculo[g.Key.Ruta].Fecha, g.Count())).ToArray())).ToArray();
        await tx.CommitAsync(ct); return salida;
    }

    public async Task<ResultadoFlotaDatos?> ConfirmarFlotaAsync(ContextoUsuario actor, Guid vehiculoId,
        string accion, ComandoFlotaDatos datos, CancellationToken ct)
    {
        var op = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        if (vehiculoId == Guid.Empty || datos.ComandoId == Guid.Empty || datos.Version < 0
            || string.IsNullOrWhiteSpace(datos.Motivo) || datos.Motivo.Length > 500
            || accion is not ("verificar-inicial" or "retorno" or "liberar" or "anular" or "conciliar" or "recibir-carga-inicial")
            || accion != "recibir-carga-inicial" && (datos.EnvioId is not null || datos.VersionEnvio is not null))
            throw new ArgumentException("Indicá acción, comando, versión, ocupación exacta y motivo de hasta 500 caracteres.");
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(op, ct); await Bloquear(op, ct);
        var anterior = await db.HechosFlota.SingleOrDefaultAsync(h => h.OperadorId == op && h.ComandoId == datos.ComandoId, ct);
        if (anterior is not null)
        {
            if (anterior.Hash != HashFlota(actor, vehiculoId, accion, datos))
                throw new ConflictoRepartoException("El comando ya fue usado con otro contenido, ocupación o actor.");
            var repetido = JsonSerializer.Deserialize<ResultadoFlotaDatos>(anterior.Respuesta, JsonFlota)!;
            await tx.CommitAsync(ct); return repetido;
        }
        var vehiculo = await db.Vehiculos.SingleOrDefaultAsync(v => v.OperadorId == op && v.Id == vehiculoId, ct);
        if (vehiculo is null) return null;
        var ahora = reloj.GetUtcNow().ToUniversalTime();
        OcupacionRuta? ocupacion = null;
        Guid? rutaConciliada = null;
        Dictionary<Guid, long>? versionesBultos = null;
        if (accion == "verificar-inicial")
        {
            ExigirFlota(datos.OcupacionId is null && vehiculo.Version == datos.Version, "El vehículo cambió; consultá su versión actual antes de confirmar.");
            ExigirFlota(!await db.OcupacionesRuta.AnyAsync(o => o.OperadorId == op && o.VehiculoId == vehiculoId
                && o.LiberadaEnUtc == null && o.AnuladaEnUtc == null, ct), "Hay ocupaciones pendientes que deben resolverse antes de acreditar disponibilidad inicial.");
            ExigirFlota(!await CargaPendiente(op).AnyAsync(b => db.HojasRuta.Any(h => h.OperadorId == op && h.Id == b.RutaCustodiaId && h.VehiculoId == vehiculoId), ct),
                "El vehículo conserva carga pendiente de recepción o conciliación.");
            Cambiar(() => vehiculo.ConfirmarVerificacionInicial(actor.AccesoId, ahora));
        }
        else if (accion == "recibir-carga-inicial")
        {
            ExigirFlota(vehiculo.VerificacionInicialEnUtc is null && vehiculo.Version == datos.Version && datos.OcupacionId is null,
                "Esta conciliación solo admite carga histórica de un vehículo pendiente de verificación inicial, con su versión vigente.");
            if (datos.EnvioId is null || datos.VersionEnvio is null or < 0) throw new ArgumentException("Indicá envío y versión vigentes para la recepción física inicial.");
            var envio = await db.Envios.SingleOrDefaultAsync(e => e.OperadorId == op && e.Id == datos.EnvioId, ct);
            if (envio is null) return null;
            ExigirFlota(envio.Version == datos.VersionEnvio && envio.Estado != EstadoEnvio.Extraviado, "El envío cambió o requiere otra conciliación operativa.");
            var bultos = await CargaPendiente(op).Where(b => b.EnvioId == envio.Id).ToArrayAsync(ct);
            ExigirFlota(bultos.Length > 0 && bultos.Select(b => b.RutaCustodiaId).Distinct().Count() == 1, "La carga no corresponde a una única ruta histórica cerrada.");
            rutaConciliada = bultos[0].RutaCustodiaId;
            ExigirFlota(await db.HojasRuta.AnyAsync(h => h.OperadorId == op && h.Id == rutaConciliada && h.VehiculoId == vehiculoId && h.Estado == EstadoHojaRuta.Finalizada, ct)
                && !await db.OcupacionesRuta.AnyAsync(o => o.OperadorId == op && o.RutaId == rutaConciliada, ct),
                "Usá la recepción de depósito vigente: esta excepción solo concilia rutas cerradas anteriores a B2 sin ocupación registrada.");
            versionesBultos = bultos.ToDictionary(b => b.Id, b => b.Version);
            foreach (var b in bultos)
            {
                db.Attach(b);
                if (b.Custodia == EstadoCustodiaBulto.Repartidor) b.RecibirTrasIncidenciaDeCierre(b.AccesoCustodioId!.Value, rutaConciliada!.Value);
                else b.RecibirEnDeposito();
            }
        }
        else
        {
            ocupacion = await db.OcupacionesRuta.SingleOrDefaultAsync(o => o.OperadorId == op && o.VehiculoId == vehiculoId && o.Id == datos.OcupacionId, ct);
            if (ocupacion is null) return null;
            ExigirFlota(ocupacion.Version == datos.Version && ocupacion.Activa, "La ocupación cambió o ya fue resuelta; no se liberó otro trabajo.");
            var hoja = await db.HojasRuta.Include(h => h.Paradas).SingleAsync(h => h.OperadorId == op && h.Id == ocupacion.RutaId, ct);
            switch (accion)
            {
                case "retorno":
                    ExigirFlota(hoja.InicioEnUtc is not null, "La autorización no inici? jornada; corresponde anularla sin inventar un retorno.");
                    Cambiar(() => ocupacion.ConfirmarRetorno(actor.AccesoId, ahora)); break;
                case "liberar":
                    var pendientes = await CargaPendiente(op).AnyAsync(b => b.RutaCustodiaId == hoja.Id, ct);
                    Cambiar(() => ocupacion.Liberar(actor.AccesoId, ahora, hoja.Estado == EstadoHojaRuta.Finalizada, !pendientes));
                    if (vehiculo.VerificacionInicialEnUtc is null) vehiculo.ConfirmarVerificacionInicial(actor.AccesoId, ahora);
                    break;
                case "anular":
                    ExigirFlota(!await db.Bultos.AnyAsync(b => b.OperadorId == op && b.RutaCustodiaId == hoja.Id, ct)
                        && !await db.OperacionesJornada.AnyAsync(o => o.OperadorId == op && o.RutaId == hoja.Id && o.Resultado == "Aplicada"
                            && (o.Tipo == "InicioJornada" || o.Tipo == "CargarBulto" || o.Tipo == "ConfirmarCarga"), ct),
                        "La autorización ya registra inicio, salida o carga física; no se puede anular.");
                    Cambiar(() => hoja.AnularDespachoSinInicio());
                    Cambiar(() => ocupacion.Anular(actor.AccesoId, ahora)); break;
                case "conciliar":
                    ExigirFlota(!await db.OcupacionesRuta.AnyAsync(o => o.OperadorId == op && o.Id != ocupacion.Id
                        && o.LiberadaEnUtc == null && o.AnuladaEnUtc == null
                        && (o.VehiculoId == ocupacion.VehiculoId || o.RepartidorAccesoId == ocupacion.RepartidorAccesoId), ct),
                        "Hay asignaciones legadas incompatibles. Resolvelas expresamente antes de confirmar una ocupación vigente.");
                    Cambiar(() => ocupacion.Conciliar(actor.AccesoId, ahora));
                    if (vehiculo.VerificacionInicialEnUtc is null) vehiculo.ConfirmarVerificacionInicial(actor.AccesoId, ahora);
                    break;
            }
        }
        var respuesta = new ResultadoFlotaDatos(datos.ComandoId, vehiculoId, ocupacion?.RutaId ?? rutaConciliada, ocupacion?.Id,
            ocupacion?.Version ?? vehiculo.Version, accion, ahora, datos.EnvioId, datos.VersionEnvio, versionesBultos);
        RegistrarHechoFlota(actor, vehiculoId, accion, datos, respuesta);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return respuesta;
    }

    // Custody is authoritative; an explicit, current loss confirmation is an existing reconciliation, not an invented unloading.
    private IQueryable<Bulto> CargaPendiente(Guid op) => db.Bultos.AsNoTracking().Where(b => b.OperadorId == op
        && (b.Custodia == EstadoCustodiaBulto.Repartidor || b.Custodia == EstadoCustodiaBulto.PendienteDeposito)
        && !db.ConfirmacionesExtravioEnvio.Any(c => c.OperadorId == op && c.EnvioId == b.EnvioId
            && db.Envios.Any(e => e.OperadorId == op && e.Id == b.EnvioId && e.Estado == EstadoEnvio.Extraviado && e.Version == c.VersionNueva)));
    private static string HashFlota(ContextoUsuario actor, Guid vehiculoId, string accion, ComandoFlotaDatos datos) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { actor.AccesoId, actor.UsuarioId, actor.OperadorId, vehiculoId, accion, datos }, JsonFlota))));
    private void RegistrarHechoFlota(ContextoUsuario actor, Guid vehiculoId, string accion, ComandoFlotaDatos datos, ResultadoFlotaDatos respuesta) =>
        db.HechosFlota.Add(new() { OperadorId = actor.OperadorId!.Value, ComandoId = datos.ComandoId, UsuarioId = actor.UsuarioId,
            AccesoId = actor.AccesoId, VehiculoId = vehiculoId, RutaId = respuesta.RutaId, OcupacionId = respuesta.OcupacionId,
            Accion = accion, Hash = HashFlota(actor, vehiculoId, accion, datos), Motivo = datos.Motivo, VersionAnterior = datos.Version,
            ConfirmadoEnUtc = respuesta.ConfirmadoEnUtc, Respuesta = JsonSerializer.Serialize(respuesta, JsonFlota) });
    private static void ExigirFlota(bool condicion, string mensaje) { if (!condicion) throw new ConflictoRepartoException(mensaje); }
    private static void Cambiar(Action accion)
    { try { accion(); } catch (InvalidOperationException ex) { throw new ConflictoRepartoException(ex.Message); } }
}
