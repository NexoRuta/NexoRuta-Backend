using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IRolesRepository
{
    Task<IReadOnlyList<RolResumen>> ListarAsync(ContextoUsuario actor, CancellationToken ct);
    Task<IReadOnlyList<PermisoResumen>> PermisosAsync(ContextoUsuario actor, CancellationToken ct);
    Task<Rol?> ObtenerAsync(ContextoUsuario actor, Guid id, CancellationToken ct);
    Task CrearAsync(Rol role, CancellationToken ct);
    Task<bool> RenombrarAsync(ContextoUsuario actor, Guid id, string nombre, CancellationToken ct);
    Task<bool> EliminarAsync(ContextoUsuario actor, Guid id, CancellationToken ct);
    Task<bool> ActualizarPermisosAsync(ContextoUsuario actor, Guid id, IReadOnlyList<string> codigos, CancellationToken ct);
    Task<bool> AsignarAsync(ContextoUsuario actor, Guid accesoId, Guid? rolId, bool puedeAsignarRolProtegido, CancellationToken ct);
}
