namespace NexoRuta.Domain.Administracion;

public sealed class Comercio
{
    public Guid Id { get; private set; }
    public string Nombre { get; private set; } = "";

    private Comercio() { } // EF Core

    public Comercio(string nombre, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre es obligatorio.", nameof(nombre));

        if (id == Guid.Empty) throw new ArgumentException("The organization ID cannot be empty.", nameof(id));
        Id = id ?? Guid.CreateVersion7();
        Nombre = nombre.Trim();
    }
}
