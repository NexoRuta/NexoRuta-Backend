namespace NexoRuta.Infrastructure.Outbox;

public static class OutboxMessageTypes
{
    public const string TrackingRequested = "GenerarSeguimiento";
    public const string ShipmentCreated = "EnvioCreado";
}

public enum OutboxDestino
{
    RabbitMq,
    Kafka
}

public enum OutboxEstado
{
    Pendiente,
    Procesando,
    Publicado,
    Error
}

public sealed class OutboxMessage
{
    public Guid Id { get; private set; }

    public Guid OperadorId { get; private set; }

    public Guid EnvioId { get; private set; }

    public Guid? ProcesadorId { get; private set; }

    public string Tipo { get; private set; } = string.Empty;

    public OutboxDestino Destino { get; private set; }

    public OutboxEstado Estado { get; private set; }

    public string Payload { get; private set; } = string.Empty;

    public int Intentos { get; private set; }

    public DateTimeOffset CreadoEnUtc { get; private set; }

    public DateTimeOffset ProximoIntentoEnUtc { get; private set; }

    public DateTimeOffset? PublicadoEnUtc { get; private set; }


    public DateTimeOffset? ReservaHastaUtc { get; private set; }

    public string? UltimoError { get; private set; }
    private OutboxMessage() { }

    public OutboxMessage(
        Guid operadorId,
        Guid envioId,
        string tipo,
        OutboxDestino destino,
        string payload)
    {
        if (operadorId == Guid.Empty || envioId == Guid.Empty)
            throw new ArgumentException("El operador y el envío son obligatorios.");

        if (string.IsNullOrWhiteSpace(tipo) ||
            string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("El tipo y el payload son obligatorios.");

        Id = Guid.CreateVersion7();

        OperadorId = operadorId;
        EnvioId = envioId;

        Tipo = tipo;
        Destino = destino;
        Payload = payload;

        Estado = OutboxEstado.Pendiente;
        Intentos = 0;

        CreadoEnUtc = DateTimeOffset.UtcNow;
        ProximoIntentoEnUtc = CreadoEnUtc;
    }
}
