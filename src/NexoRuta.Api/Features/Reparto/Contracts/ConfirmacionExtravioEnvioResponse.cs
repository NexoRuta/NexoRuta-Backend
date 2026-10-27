using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed record ConfirmacionExtravioEnvioResponse(Guid EnvioId, Guid ConfirmacionId, Guid ComandoId,
    string EstadoConfirmado, long VersionAnterior, long VersionNueva, string Motivo, DateTimeOffset ConfirmadaEnUtc,
    string EstadoActual, long VersionActual, bool Repetida)
{
    public static ConfirmacionExtravioEnvioResponse Desde(ConfirmacionExtravioEnvioDatos x) =>
        new(x.EnvioId, x.ConfirmacionId, x.ComandoId, x.EstadoConfirmado, x.VersionAnterior, x.VersionNueva,
            x.Motivo, x.ConfirmadaEnUtc, x.EstadoActual, x.VersionActual, x.Repetida);
}
