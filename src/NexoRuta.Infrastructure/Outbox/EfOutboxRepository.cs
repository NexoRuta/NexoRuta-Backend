using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Outbox;

public sealed class EfOutboxRepository(NexoRutaDbContext db)
{
    public async Task<OutboxMessage?> ReservarSiguienteAsync(
        Guid procesadorId,
        OutboxDestino destino,
        CancellationToken cancellationToken = default)
    {
        if (procesadorId == Guid.Empty)
            throw new ArgumentException(
                "El identificador del procesador es obligatorio.",
                nameof(procesadorId));

        if (!Enum.IsDefined(destino))
            throw new ArgumentOutOfRangeException(nameof(destino));

        var ahora = DateTimeOffset.UtcNow;
        var reservaHasta = ahora.AddMinutes(2);

        // Each dispatcher only leases messages for its own transport.
        var candidatos = await db.OutboxMessages
            .AsNoTracking()
            .Where(x =>
                x.Destino == destino &&
                (
                    (
                        (x.Estado == OutboxEstado.Pendiente ||
                         x.Estado == OutboxEstado.Error) &&
                        x.ProximoIntentoEnUtc <= ahora
                    ) ||
                    (
                        x.Estado == OutboxEstado.Procesando &&
                        x.ReservaHastaUtc <= ahora
                    )
                ))
            .OrderBy(x => x.CreadoEnUtc)
            .Select(x => x.Id)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var id in candidatos)
        {
            // UPDATE condicional: solo uno de los Workers gana.
            var actualizados = await db.OutboxMessages
                .Where(x =>
                    x.Id == id &&
                    x.Destino == destino &&
                    (
                        (
                            (x.Estado == OutboxEstado.Pendiente ||
                             x.Estado == OutboxEstado.Error) &&
                            x.ProximoIntentoEnUtc <= ahora
                        ) ||
                        (
                            x.Estado == OutboxEstado.Procesando &&
                            x.ReservaHastaUtc <= ahora
                        )
                    ))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        x => x.Estado,
                        OutboxEstado.Procesando)
                    .SetProperty(
                        x => x.ProcesadorId,
                        (Guid?)procesadorId)
                    .SetProperty(
                        x => x.ReservaHastaUtc,
                        (DateTimeOffset?)reservaHasta)
                    .SetProperty(
                        x => x.Intentos,
                        x => x.Intentos < int.MaxValue ? x.Intentos + 1 : x.Intentos),
                    cancellationToken);

            if (actualizados == 1)
            {
                return await db.OutboxMessages
                    .AsNoTracking()
                    .SingleAsync(
                        x => x.Id == id,
                        cancellationToken);
            }
        }

        return null;
    }
    public async Task<bool> MarcarPublicadoAsync(
    Guid mensajeId,
    Guid procesadorId,
    CancellationToken cancellationToken = default)
    {
        var ahora = DateTimeOffset.UtcNow;

        var actualizados = await db.OutboxMessages
            .Where(x =>
                x.Id == mensajeId &&
                x.Estado == OutboxEstado.Procesando &&
                x.ProcesadorId == procesadorId &&
                x.ReservaHastaUtc > ahora)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    x => x.Estado,
                    OutboxEstado.Publicado)
                .SetProperty(
                    x => x.PublicadoEnUtc,
                    (DateTimeOffset?)ahora)
                .SetProperty(
                    x => x.ProcesadorId,
                    (Guid?)null)
                .SetProperty(
                    x => x.ReservaHastaUtc,
                    (DateTimeOffset?)null)
                .SetProperty(
                    x => x.UltimoError,
                    (string?)null),
                cancellationToken);


        return actualizados == 1;
    }
    public async Task<bool> MarcarErrorAsync(
    Guid mensajeId,
    Guid procesadorId,
    string error,
    CancellationToken cancellationToken = default, TimeSpan? demora = null)
    {
        var ahora = DateTimeOffset.UtcNow;
        var proximoIntento = ahora.Add(demora ?? TimeSpan.FromSeconds(30));

        var mensajeError = string.IsNullOrWhiteSpace(error)
            ? "Error de publicación"
            : error[..Math.Min(error.Length, 2000)];

        var actualizados = await db.OutboxMessages
            .Where(x =>
                x.Id == mensajeId &&
                x.Estado == OutboxEstado.Procesando &&
                x.ProcesadorId == procesadorId &&
                x.ReservaHastaUtc > ahora)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    x => x.Estado,
                    OutboxEstado.Error)
                .SetProperty(
                    x => x.ProximoIntentoEnUtc,
                    proximoIntento)
                .SetProperty(
                    x => x.UltimoError,
                    mensajeError)
                .SetProperty(
                    x => x.ProcesadorId,
                    (Guid?)null)
                .SetProperty(
                    x => x.ReservaHastaUtc,
                    (DateTimeOffset?)null),
                cancellationToken);

        return actualizados == 1;
    }
}
