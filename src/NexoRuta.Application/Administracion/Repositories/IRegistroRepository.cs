using NexoRuta.Application.Administracion.Context;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IRegistroRepository
{
    Task<IdentidadSinOrganizacion> RegistrarAsync(string email, string password, CancellationToken ct);
    Task<IdentidadSinOrganizacion?> ObtenerSinOrganizacionAsync(Guid usuarioId, CancellationToken ct);
}
