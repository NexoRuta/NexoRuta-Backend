namespace NexoRuta.Application.Operacion.Results;

public sealed record ResultadoHistorialOperativo(
    string EstadoProyeccion, DateTimeOffset VerificadaEnUtc, int RetencionDias,
    int Offset, int Limite, IReadOnlyList<EventoOperativo> Datos);
