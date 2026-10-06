namespace NexoRuta.Domain.Administracion;

public sealed class OperadorComercio
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid ComercioId { get; private set; }

    public OperadorComercio(Guid operadorId, Guid comercioId)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (comercioId == Guid.Empty)
            throw new ArgumentException("El comercio es obligatorio.", nameof(comercioId));

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        ComercioId = comercioId;
    }
}
