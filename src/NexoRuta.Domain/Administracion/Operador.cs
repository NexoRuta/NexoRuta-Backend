namespace NexoRuta.Domain.Administracion;

public sealed class Operador
{
    public Guid Id { get; private set; }
    public string Nombre { get; private set; }

    public Operador(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre es obligatorio.", nameof(nombre));

        Id = Guid.CreateVersion7();
        Nombre = nombre.Trim();
    }
}
