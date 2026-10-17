
namespace NexoRuta.Domain.Administracion;

public sealed class RolPermiso
{
    public Guid RolId { get; private set; }

    public Guid PermisoId { get; private set; }

    private RolPermiso() { } // EF Core

    public RolPermiso(Guid rolId, Guid permisoId)
    {
        if (rolId == Guid.Empty)
            throw new ArgumentException(
                "El rol es obligatorio.",
                nameof(rolId));

        if (permisoId == Guid.Empty)
            throw new ArgumentException(
                "El permiso es obligatorio.",
                nameof(permisoId));

        RolId = rolId;
        PermisoId = permisoId;
    }
}
