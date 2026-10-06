namespace NexoRuta.Domain.Envios;

public sealed class Envio
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid OperadorComercioId { get; private set; }
    public Guid DestinatarioId { get; private set; }
    public Guid DireccionId { get; private set; }
    public EstadoEnvio Estado { get; private set; }

    public Envio(Guid operadorId, Guid operadorComercioId, Guid destinatarioId, Guid direccionId)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (operadorComercioId == Guid.Empty)
            throw new ArgumentException("El vínculo entre operador y comercio es obligatorio.", nameof(operadorComercioId));
        if (destinatarioId == Guid.Empty)
            throw new ArgumentException("El destinatario es obligatorio.", nameof(destinatarioId));
        if (direccionId == Guid.Empty)
            throw new ArgumentException("La dirección es obligatoria.", nameof(direccionId));

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        OperadorComercioId = operadorComercioId;
        DestinatarioId = destinatarioId;
        DireccionId = direccionId;
        Estado = EstadoEnvio.Admitido;
    }
}
