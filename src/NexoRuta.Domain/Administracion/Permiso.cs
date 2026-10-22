
namespace NexoRuta.Domain.Administracion;

public sealed class Permiso
{
    public Guid Id { get; private set; }

    public string Codigo { get; private set; } = string.Empty;

    public string Nombre { get; private set; } = string.Empty;

    private Permiso() { } // EF Core

    public Permiso(string codigo, string nombre)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException(
                "El código del permiso es obligatorio.",
                nameof(codigo));

        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException(
                "El nombre del permiso es obligatorio.",
                nameof(nombre));

        Id = Guid.CreateVersion7();
        Codigo = codigo.Trim().ToLowerInvariant();
        Nombre = nombre.Trim();
    }
}
