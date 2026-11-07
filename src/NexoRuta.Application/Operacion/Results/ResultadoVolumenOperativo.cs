namespace NexoRuta.Application.Operacion.Results;

public sealed record ResultadoVolumenOperativo(
    string EstadoProyeccion, DateTimeOffset VerificadaEnUtc, int RetencionDias,
    DateTimeOffset Desde, DateTimeOffset Hasta, IReadOnlyDictionary<string, long> VolumenPorTipo);
