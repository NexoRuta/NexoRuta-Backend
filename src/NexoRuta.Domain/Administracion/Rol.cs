
namespace NexoRuta.Domain.Administracion;

public sealed class Rol
{
    public Guid Id { get; private set; }

    public string Codigo { get; private set; } = string.Empty;

    public string Nombre { get; private set; } = string.Empty;

    public TipoAccesoUsuario TipoOrganizacion { get; private set; }

    private Rol() { } // EF Core

    public Rol(
        string codigo,
        string nombre,
        TipoAccesoUsuario tipoOrganizacion)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException(
                "El código del rol es obligatorio.",
                nameof(codigo));

        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException(
                "El nombre del rol es obligatorio.",
                nameof(nombre));

        if (!Enum.IsDefined(tipoOrganizacion))
            throw new ArgumentOutOfRangeException(
                nameof(tipoOrganizacion));

        Id = Guid.CreateVersion7();
        Codigo = codigo.Trim().ToLowerInvariant();
        Nombre = nombre.Trim();
        TipoOrganizacion = tipoOrganizacion;
    }
}
