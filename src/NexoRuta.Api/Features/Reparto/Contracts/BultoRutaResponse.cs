using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record BultoRutaResponse(Guid Id, string Codigo, decimal PesoGramos, decimal VolumenCm3, string Custodia, long Version)
{
    public static BultoRutaResponse Desde(BultoRutaDatos x) => new(x.Id, x.Codigo, x.PesoGramos, x.VolumenCm3, x.Custodia, x.Version);
}
