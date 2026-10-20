namespace NexoRuta.Application.Reparto.Dtos;

public sealed record ReasignarEnvioDatos(Guid RutaDestinoId, long VersionEnvio, long VersionRutaDestino,
    DateTimeOffset LlegadaEstimadaUtc, string Motivo);
