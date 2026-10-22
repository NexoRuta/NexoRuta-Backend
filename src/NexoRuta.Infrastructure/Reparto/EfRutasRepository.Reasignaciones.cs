using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Notificaciones;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfRutasRepository
{
    public async Task<HojaRutaDatos?> ObtenerPropiaAsync(Guid operadorId, Guid repartidorId, Guid rutaId, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        var hoja = await db.HojasRuta.AsNoTracking().Include(x => x.Paradas).SingleOrDefaultAsync(h =>
            h.OperadorId == operadorId && h.Id == rutaId && h.RepartidorAccesoId == repartidorId && h.Estado != EstadoHojaRuta.Planificada, ct);
        var salida = hoja is null ? null : await Datos(hoja, ct);
        await tx.CommitAsync(ct); return salida;
    }

    public async Task<HojaRutaDatos?> ReasignarAsync(Guid operadorId, Guid envioId, ReasignarEnvioDatos datos, CancellationToken ct)
    {
        if (datos.RutaDestinoId == Guid.Empty || datos.VersionEnvio < 0 || datos.VersionRutaDestino < 0
            || datos.LlegadaEstimadaUtc == default || string.IsNullOrWhiteSpace(datos.Motivo) || datos.Motivo.Length > 500)
            throw new ArgumentException("Indicá ruta futura, versiones, llegada estimada y motivo.");
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Bloquear(operadorId, ct);
        var envio = await db.Envios.SingleOrDefaultAsync(e => e.OperadorId == operadorId && e.Id == envioId, ct);
        if (envio is null) return null;
        if (envio.EntregaFisicaEnUtc is not null || envio.Version != datos.VersionEnvio
            || envio.Estado is not (EstadoEnvio.AsignadoRuta or EstadoEnvio.EnTransito or EstadoEnvio.NoEntregado or EstadoEnvio.Reprogramado))
            throw new ConflictoRepartoException("El envío cambió o ya no admite reasignación; después de la entrega física corresponde devolución.");
        var destino = await db.HojasRuta.Include(h => h.Paradas)
            .SingleOrDefaultAsync(h => h.Id == datos.RutaDestinoId && h.OperadorId == operadorId, ct);
        if (destino is null) return null;
        if (destino.Estado != EstadoHojaRuta.Planificada || destino.Version != datos.VersionRutaDestino
            || destino.Paradas.Any(p => p.EnvioId == envioId))
            throw new ConflictoRepartoException("La ruta destino cambió, ya fue despachada o contiene este envío.");
        var politica = await db.ConfiguracionesReparto.SingleAsync(p => p.OperadorId == operadorId && p.Version == destino.ConfiguracionVersion, ct);
        var zona = TimeZoneInfo.FindSystemTimeZoneById(politica.ZonaHoraria);
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.GetUtcNow(), zona).DateTime);
        if (destino.Fecha < hoy || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(datos.LlegadaEstimadaUtc, zona).DateTime) != destino.Fecha
            || destino.Paradas.Count > 0 && datos.LlegadaEstimadaUtc < destino.Paradas.MaxBy(p => p.Orden)!.LlegadaEstimadaUtc)
            throw new ConflictoRepartoException("La llegada debe corresponder a la fecha futura y al orden de la ruta destino.");
        if (destino.Paradas.Count(p => p.Activa) >= politica.MaximoParadas
            || !await RepartidorHabilitado(operadorId, destino.RepartidorAccesoId, ct)
            || !await db.Vehiculos.AnyAsync(v => v.OperadorId == operadorId && v.Id == destino.VehiculoId && v.Activo, ct))
            throw new ConflictoRepartoException("La ruta destino supera sus paradas o tiene repartidor/vehículo deshabilitados.");
        var ids = destino.Paradas.Where(p => p.Activa).Select(p => p.EnvioId).Append(envioId).ToArray();
        var bultos = await db.Bultos.Where(b => b.OperadorId == operadorId && ids.Contains(b.EnvioId)).ToArrayAsync(ct);
        if (!bultos.Any(b => b.EnvioId == envioId) || bultos.Sum(b => b.PesoGramos) > destino.VehiculoPesoMaximoGramos
            || bultos.Sum(b => b.LargoCentimetros * b.AnchoCentimetros * b.AltoCentimetros) > destino.VehiculoVolumenMaximoCm3)
            throw new ConflictoRepartoException("La reasignación supera el peso o volumen de la ruta destino.");
        var propias = bultos.Where(b => b.EnvioId == envioId).ToArray();
        var custodias = propias.Where(b => b.Custodia is EstadoCustodiaBulto.Repartidor or EstadoCustodiaBulto.PendienteDeposito)
            .Select(b => b.RutaCustodiaId).Where(id => id != null).Distinct().ToArray();
        if (custodias.Length > 1) throw new ConflictoRepartoException("La custodia del envío requiere revisión antes de reasignar.");
        var anteriores = await db.HojasRuta.Include(h => h.Paradas).Where(h => h.OperadorId == operadorId &&
            (h.Paradas.Any(p => p.EnvioId == envioId && p.Activa) || custodias.Contains(h.Id))).ToArrayAsync(ct);
        var actual = anteriores.SingleOrDefault(h => custodias.Contains(h.Id) && h.Estado is EstadoHojaRuta.Despachada or EstadoHojaRuta.EnCurso)
            ?? anteriores.SingleOrDefault(h => h.Paradas.Any(p => p.EnvioId == envioId && p.Activa) && h.Estado is EstadoHojaRuta.Despachada or EstadoHojaRuta.EnCurso);
        foreach (var anterior in anteriores.Where(h => h.Paradas.Any(p => p.EnvioId == envioId && p.Activa)))
            anterior.SuspenderParada(envioId, datos.Motivo);
        if (envio.Estado != EstadoEnvio.Reprogramado) envio.CambiarEstado(EstadoEnvio.Reprogramado, envio.Version);
        // Libera el índice de parada activa antes de insertar la nueva, dentro de la misma transacción.
        await db.SaveChangesAsync(ct);
        destino.AgregarParada(envioId, datos.LlegadaEstimadaUtc);
        envio.CambiarEstado(EstadoEnvio.AsignadoRuta, envio.Version);
        if (actual is not null)
        {
            var aviso = new AvisoReparto(operadorId, actual.RepartidorAccesoId, actual.Id, envioId, envio.Version,
                "Reasignacion", datos.Motivo, reloj.GetUtcNow());
            db.AvisosReparto.Add(aviso);
            db.OutboxMessages.Add(OutboxMessage.ForShipment(operadorId, envioId, OutboxMessageTypes.DeliveryChanged,
                OutboxDestino.PushFcm, JsonSerializer.Serialize(new { avisoId = aviso.Id })));
        }
        await db.SaveChangesAsync(ct);
        var salida = await Datos(destino, ct);
        await tx.CommitAsync(ct); return salida;
    }
}
