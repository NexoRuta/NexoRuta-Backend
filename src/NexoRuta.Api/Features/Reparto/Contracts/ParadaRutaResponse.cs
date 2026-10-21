using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record ParadaRutaResponse(Guid EnvioId, int Orden, bool Activa, string? MotivoSuspension,
    DateTimeOffset LlegadaEstimadaUtc, string Estado, long EnvioVersion, string Destinatario,
    string Direccion, double Latitud, double Longitud, IReadOnlyList<BultoRutaResponse> Bultos, ReglasEnvioResponse Reglas)
{
    public static ParadaRutaResponse Desde(ParadaRutaDatos x) => new(x.EnvioId, x.Orden, x.Activa,
        x.MotivoSuspension, x.LlegadaEstimadaUtc, x.Estado, x.EnvioVersion, x.Destinatario,
        x.Direccion, x.Latitud, x.Longitud, x.Bultos.Select(BultoRutaResponse.Desde).ToArray(), ReglasEnvioResponse.Desde(x.Reglas));
}
