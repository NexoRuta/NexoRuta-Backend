using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Repositories;

public sealed record OrganizacionAdministrativa
{
    public Guid Id { get; init; }
    public string Nombre { get; init; } = "";
    public string Estado { get; init; } = "Activa";
    public long Version { get; init; }
}
public sealed record PaginaOrganizaciones(IReadOnlyList<OrganizacionAdministrativa> Items, int Pagina, int TamanoPagina, int Total);
public sealed record AltaOrganizacion(OrganizacionAdministrativa Organizacion, Guid UsuarioId, Guid AccesoId, Guid RolId);

public interface IPlataformaRepository
{
    Task<ContextoPlataforma?> ObtenerAccesoAsync(Guid accesoId, CancellationToken ct);
    Task InicializarAsync(string email, string password, CancellationToken ct = default, bool sincronizarPasswordExistente = false);
    Task<AltaOrganizacion> CrearAsync(TipoAccesoUsuario tipo, string nombre, string email, string password, CancellationToken ct);
    Task<PaginaOrganizaciones> ListarAsync(TipoAccesoUsuario tipo, EstadoOrganizacion? estado, int pagina, int tamanoPagina, CancellationToken ct);
    Task<OrganizacionAdministrativa?> ObtenerAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct);
}
