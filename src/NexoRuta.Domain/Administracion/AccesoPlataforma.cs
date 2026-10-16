namespace NexoRuta.Domain.Administracion;

public sealed class AccesoPlataforma
{
    // ponytail: this slice initializes one platform account; multi-account administration needs an explicit provisioning workflow and removal of the singleton constraint.
    public const int CuentaInicial = 1;
    public int Id { get; private set; } = CuentaInicial;
    public Guid AccesoId { get; private set; } = Guid.CreateVersion7();
    public Guid UsuarioId { get; private set; }
    public Guid RolId { get; private set; }
    public bool Activo { get; private set; } = true;
    public long VersionSesion { get; private set; }

    private AccesoPlataforma() { }

    public AccesoPlataforma(Usuario usuario, RolPlataforma rol)
    {
        UsuarioId = usuario.Id;
        RolId = rol.Id;
    }

    public void CambiarEstado(bool activo)
    {
        if (Activo == activo) return;
        Activo = activo;
        VersionSesion = checked(VersionSesion + 1);
    }
}
