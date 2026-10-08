namespace NexoRuta.Domain.Envios;

public sealed class Envio
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid ComercioId { get; private set; }
    public Guid CreadoPorUsuarioId { get; private set; }
    public Guid DestinatarioId { get; private set; }
    public Guid DireccionId { get; private set; }
    public EstadoEnvio Estado { get; private set; }

    public Envio(
        Guid operadorId,
        Guid comercioId,
        Guid creadoPorUsuarioId,
        Guid destinatarioId,
        Guid direccionId)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (comercioId == Guid.Empty)
            throw new ArgumentException("El comercio es obligatorio.", nameof(comercioId));
        if (creadoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("El usuario creador es obligatorio.", nameof(creadoPorUsuarioId));
        if (destinatarioId == Guid.Empty)
            throw new ArgumentException("El destinatario es obligatorio.", nameof(destinatarioId));
        if (direccionId == Guid.Empty)
            throw new ArgumentException("La dirección es obligatoria.", nameof(direccionId));

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        ComercioId = comercioId;
        CreadoPorUsuarioId = creadoPorUsuarioId;
        DestinatarioId = destinatarioId;
        DireccionId = direccionId;
        Estado = EstadoEnvio.Admitido;
    }
}
