namespace NexoRuta.Domain.Envios;

public sealed class Destinatario
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public string Nombre { get; private set; }

    public Destinatario(Guid operadorId, string nombre)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre es obligatorio.", nameof(nombre));

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        Nombre = nombre.Trim();
    }
}
