using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record BultoRutaResponse(Guid Id, string Codigo, decimal PesoGramos, decimal VolumenCm3, string Custodia, long Version)
{
    public bool EnEstaRuta { get; init; } public string Resultado { get; init; } = "Pendiente";
    public static BultoRutaResponse Desde(BultoRutaDatos x) => new(x.Id, x.Codigo, x.PesoGramos, x.VolumenCm3, x.Custodia, x.Version) { Resultado = x.Resultado, EnEstaRuta = x.EnEstaRuta };
}
