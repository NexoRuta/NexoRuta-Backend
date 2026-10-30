using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed record DecisionDevolucionEnvioResponse(Guid EnvioId, Guid DecisionId, Guid ComandoId,
    string EstadoDecision, long VersionDecision, string Motivo, DateTimeOffset DecididaEnUtc,
    string EstadoActual, long VersionActual, bool Repetida)
{
    public static DecisionDevolucionEnvioResponse Desde(DecisionDevolucionEnvioDatos x) =>
        new(x.EnvioId, x.DecisionId, x.ComandoId, x.EstadoDecision, x.VersionDecision, x.Motivo,
            x.DecididaEnUtc, x.EstadoActual, x.VersionActual, x.Repetida);
}
