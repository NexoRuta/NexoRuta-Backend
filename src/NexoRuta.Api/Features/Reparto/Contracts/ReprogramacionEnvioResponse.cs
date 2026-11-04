using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed record ReprogramacionEnvioResponse(Guid EnvioId, long VersionEnvio, int IntentosFallidos,
    int MaximoIntentos, DateTimeOffset? ProximoIntentoElegibleUtc)
{
    public static ReprogramacionEnvioResponse Desde(ReprogramacionEnvioDatos x) =>
        new(x.EnvioId, x.VersionEnvio, x.IntentosFallidos, x.MaximoIntentos, x.ProximoIntentoElegibleUtc);
}
