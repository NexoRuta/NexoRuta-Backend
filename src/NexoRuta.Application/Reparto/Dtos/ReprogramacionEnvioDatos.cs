namespace NexoRuta.Application.Reparto.Dtos;

public sealed record ReprogramacionEnvioDatos(Guid EnvioId, long VersionEnvio, int IntentosFallidos,
    int MaximoIntentos, DateTimeOffset? ProximoIntentoElegibleUtc);
