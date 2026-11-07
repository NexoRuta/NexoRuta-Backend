using NexoRuta.Application.Operacion;
using NexoRuta.Application.Operacion.Results;

namespace NexoRuta.Api.Features.Operacion.Contracts.Responses;

public sealed record HistorialOperativoResponse(
    string EstadoProyeccion,
    DateTimeOffset VerificadaEnUtc,
    int RetencionDias,
    int Offset,
    int Limite,
    IReadOnlyList<EventoOperativo> Datos)
{
    public static HistorialOperativoResponse Desde(ResultadoHistorialOperativo result)
        => new(result.EstadoProyeccion, result.VerificadaEnUtc, result.RetencionDias,
            result.Offset, result.Limite, result.Datos);
}
