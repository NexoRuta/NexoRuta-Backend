namespace NexoRuta.Domain.Administracion;

// Platform capabilities never belong to CatalogoPermisos or organization roles.
public sealed class RolPlataforma
{
    public const string GestionarOrganizaciones = "plataforma.organizaciones.gestionar";
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public bool EsProtegidoSistema { get; private set; } = true;
    public bool PuedeGestionarOrganizaciones { get; private set; } = true;
}
