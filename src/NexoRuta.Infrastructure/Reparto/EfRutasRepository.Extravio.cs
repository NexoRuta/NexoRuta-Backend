using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
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
    public async Task<ConfirmacionExtravioEnvioDatos?> ConfirmarExtravioAsync(ContextoUsuario actor, Guid envioId,
        ConfirmarExtravioEnvioDatos datos, CancellationToken ct)
    {
        var operadorId = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        var motivo = datos.Motivo?.Trim();
        if (envioId == Guid.Empty || datos.VersionEnvio < 0 || datos.ComandoId == Guid.Empty
            || string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500)
            throw new ArgumentException("Indicá un motivo de hasta 500 caracteres y la versión y comando vigentes.");

        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct);
        await Bloquear(operadorId, ct);

        var existente = await db.ConfirmacionesExtravioEnvio.SingleOrDefaultAsync(x =>
            x.OperadorId == operadorId && x.ComandoId == datos.ComandoId, ct);
        if (existente is not null)
        {
            if (existente.EnvioId != envioId || existente.VersionAnterior != datos.VersionEnvio
                || existente.Motivo != motivo || existente.UsuarioId != actor.UsuarioId
                || existente.AccesoId != actor.AccesoId || existente.OrganizacionId != operadorId)
                throw new ConflictoRepartoException("El identificador del comando ya fue usado con otro contenido o contexto.");
            var actual = await db.Envios.SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == envioId, ct);
            if (actual is null) return null;
            var replay = ResultadoExtravio(existente, actual, true);
            await tx.CommitAsync(ct);
            return replay;
        }

        var bloqueado = await db.Envios.FromSqlInterpolated(
                $"SELECT * FROM public.\"Envios\" WHERE \"OperadorId\"={operadorId} AND \"Id\"={envioId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (bloqueado is null) return null;
        await db.Entry(bloqueado).ReloadAsync(ct);
        if (bloqueado.Version != datos.VersionEnvio
            || bloqueado.Estado is not (EstadoEnvio.EnDeposito or EstadoEnvio.AsignadoRuta
                or EstadoEnvio.EnTransito or EstadoEnvio.EnDevolucion)
            || bloqueado.EntregaFisicaEnUtc is not null || bloqueado.EntregaFisicaRecibidaEnUtc is not null)
            throw new ConflictoRepartoException("El envío cambió o su estado actual no admite confirmar el extravío.");

        var hojasConParadaActiva = await db.HojasRuta.Include(x => x.Paradas).Where(x => x.OperadorId == operadorId
            && x.Paradas.Any(p => p.EnvioId == envioId && p.Activa)).ToArrayAsync(ct);
        if (hojasConParadaActiva.Length > 1)
            throw new ConflictoRepartoException("El envío tiene más de una parada activa y requiere revisión operativa.");

        var ahora = reloj.GetUtcNow().ToUniversalTime();
        var confirmacion = new ConfirmacionExtravioEnvio(operadorId, envioId, bloqueado.VersionReglasId,
            datos.ComandoId, bloqueado.Version, motivo, actor.UsuarioId, actor.AccesoId, ahora);
        await db.SetShipmentHistoryContextAsync("ConfirmacionExtravioEnvio", confirmacion.Id, datos.ComandoId,
            ahora, cancellationToken: ct);
        db.ConfirmacionesExtravioEnvio.Add(confirmacion);
        bloqueado.ConfirmarExtravio(bloqueado.Version);

        var hojaActiva = hojasConParadaActiva.SingleOrDefault();
        if (hojaActiva is not null)
        {
            hojaActiva.SuspenderParada(envioId, "Extravío confirmado.");
            if (hojaActiva.Estado == EstadoHojaRuta.EnCurso && hojaActiva.SalidaEnUtc is not null)
            {
                var aviso = new AvisoReparto(operadorId, hojaActiva.RepartidorAccesoId, hojaActiva.Id,
                    envioId, bloqueado.Version, "ExtravioConfirmado", motivo, ahora);
                db.AvisosReparto.Add(aviso);
                db.OutboxMessages.Add(OutboxMessage.ForShipment(operadorId, envioId,
                    OutboxMessageTypes.DeliveryChanged, OutboxDestino.PushFcm,
                    JsonSerializer.Serialize(new { avisoId = aviso.Id })));
            }
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictoRepartoException("El envío o la ruta cambiaron en otra operación; actualizá el estado antes de confirmar el extravío.");
        }
        var resultado = ResultadoExtravio(confirmacion, bloqueado, false);
        await tx.CommitAsync(ct);
        return resultado;
    }

    private static ConfirmacionExtravioEnvioDatos ResultadoExtravio(
        ConfirmacionExtravioEnvio confirmacion, Envio actual, bool repetida) =>
        new(confirmacion.EnvioId, confirmacion.Id, confirmacion.ComandoId, EstadoEnvio.Extraviado.ToString(),
            confirmacion.VersionAnterior, confirmacion.VersionNueva, confirmacion.Motivo, confirmacion.ConfirmadaEnUtc,
            actual.Estado.ToString(), actual.Version, repetida);
}
