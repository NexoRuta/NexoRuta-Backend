namespace NexoRuta.Application.Operacion.Results;

public sealed record EstadoProyeccionOperativa(
    long Capturados, long Documentados, string Estado, DateTimeOffset VerificadaEnUtc);
