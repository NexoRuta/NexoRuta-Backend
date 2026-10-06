namespace NexoRuta.Domain.Envios;

public sealed class Direccion
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public string Descripcion { get; private set; }

    public Direccion(Guid operadorId, string descripcion)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (string.IsNullOrWhiteSpace(descripcion))
            throw new ArgumentException("La dirección es obligatoria.", nameof(descripcion));

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        Descripcion = descripcion.Trim();
    }
}
