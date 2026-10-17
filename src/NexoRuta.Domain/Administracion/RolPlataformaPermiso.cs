namespace NexoRuta.Domain.Administracion;

public sealed class RolPlataformaPermiso
{
    public Guid RolId { get; private set; }
    public Guid PermisoId { get; private set; }
    private RolPlataformaPermiso() { }

    public RolPlataformaPermiso(Guid rolId, Guid permisoId)
    {
        if (rolId == Guid.Empty || permisoId == Guid.Empty)
            throw new ArgumentException("A platform role and permission are required.");
        RolId = rolId;
        PermisoId = permisoId;
    }
}
