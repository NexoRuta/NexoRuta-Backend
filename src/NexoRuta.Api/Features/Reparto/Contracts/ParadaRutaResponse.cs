using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record ParadaRutaResponse(Guid EnvioId, int Orden, bool Activa, string? MotivoSuspension,
    DateTimeOffset LlegadaEstimadaUtc, string Estado, long EnvioVersion, string Destinatario,
    string Direccion, double Latitud, double Longitud, IReadOnlyList<BultoRutaResponse> Bultos, ReglasEnvioResponse Reglas,
    int IntentosFallidos, DateTimeOffset? ProximoIntentoElegibleUtc, int? ServicioMinutos = null)
{
    public bool EsRetiro { get; init; } public string ResultadoVisita { get; init; } = "Pendiente";
    public static ParadaRutaResponse Desde(ParadaRutaDatos x) => new(x.EnvioId, x.Orden, x.Activa,
        x.MotivoSuspension, x.LlegadaEstimadaUtc, x.Estado, x.EnvioVersion, x.Destinatario,
        x.Direccion, x.Latitud, x.Longitud, x.Bultos.Select(BultoRutaResponse.Desde).ToArray(), ReglasEnvioResponse.Desde(x.Reglas),
        x.IntentosFallidos, x.ProximoIntentoElegibleUtc, x.ServicioMinutos) { EsRetiro = x.EsRetiro, ResultadoVisita = x.ResultadoVisita };
}
