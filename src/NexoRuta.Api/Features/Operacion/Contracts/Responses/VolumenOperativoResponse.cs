using NexoRuta.Application.Operacion.Results;

namespace NexoRuta.Api.Features.Operacion.Contracts.Responses;

public sealed record VolumenOperativoResponse(
    string EstadoProyeccion,
    DateTimeOffset VerificadaEnUtc,
    int RetencionDias,
    DateTimeOffset Desde,
    DateTimeOffset Hasta,
    IReadOnlyDictionary<string, long> VolumenPorTipo)
{
    public static VolumenOperativoResponse FromResult(ResultadoVolumenOperativo result)
        => new(result.EstadoProyeccion, result.VerificadaEnUtc, result.RetencionDias,
            result.Desde, result.Hasta, result.VolumenPorTipo);
}
