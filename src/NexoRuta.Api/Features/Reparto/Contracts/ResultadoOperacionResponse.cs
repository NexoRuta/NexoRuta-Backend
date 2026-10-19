using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record ResultadoOperacionResponse(Guid Id, long Secuencia, string Resultado, string? Detalle, long? VersionEnvio, long VersionRuta, IReadOnlyDictionary<Guid, long> VersionesEnvio, IReadOnlyDictionary<Guid, string> EstadosEnvio)
{
    public static ResultadoOperacionResponse Desde(ResultadoOperacionReparto x) => new(x.Id, x.Secuencia, x.Resultado, x.Detalle, x.VersionEnvio, x.VersionRuta, x.VersionesEnvio, x.EstadosEnvio);
}
