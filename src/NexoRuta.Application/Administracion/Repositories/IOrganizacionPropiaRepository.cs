using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Repositories;

public sealed record OrganizacionPropia(Guid Id, string Nombre, long Version, EstadoOrganizacion Estado);

public interface IOrganizacionPropiaRepository
{
    Task<OrganizacionPropia?> ObtenerAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct);
    Task<bool> TieneDuenoRegistradoAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct);
    Task<OrganizacionPropia?> ActualizarNombreAsync(TipoAccesoUsuario tipo, Guid id, string nombre, long version, CancellationToken ct);
}
