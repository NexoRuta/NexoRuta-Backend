using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class GestionarRolesUseCase(IUsuarioActual current, IPermisosUsuarioRepository permissions, IRolesRepository roles)
{
    private async Task<ContextoUsuario> Actor(string permission, CancellationToken ct)
    {
        var actor = await current.ObtenerAsync(ct);
        if (!await permissions.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, permission, ct))
            throw new AccesoNoPermitidoException();
        return actor;
    }

    public async Task<IReadOnlyList<RolResumen>> ListarAsync(CancellationToken ct = default)
        => await roles.ListarAsync(await Actor("configuracion.gestionar", ct), ct);

    public async Task<IReadOnlyList<RolResumen>> ListarAsignablesAsync(CancellationToken ct = default)
    {
        var actor = await Actor("usuarios.gestionar", ct);
        if (await permissions.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, "configuracion.gestionar", ct))
            return await roles.ListarAsync(actor, ct);
        return (await roles.ListarAsync(actor, ct)).Where(x => !x.EsProtegidoSistema).ToArray();
    }

    public async Task<IReadOnlyList<PermisoResumen>> PermisosAsync(CancellationToken ct = default)
    {
        var actor = await Actor("configuracion.gestionar", ct);
        return await roles.PermisosAsync(actor, ct);
    }

    public async Task<Guid> CrearAsync(string code, string name, CancellationToken ct = default)
    {
        var actor = await Actor("configuracion.gestionar", ct);
        var role = new Rol(code, name, actor.OperadorId, actor.ComercioId);
        await roles.CrearAsync(role, ct);
        return role.Id;
    }

    public async Task RenombrarAsync(Guid id, string name, CancellationToken ct = default)
    {
        var actor = await Actor("configuracion.gestionar", ct);
        if (!await roles.RenombrarAsync(actor, id, name, ct))
            throw new AccesoNoPermitidoException();
    }

    public async Task EliminarAsync(Guid id, CancellationToken ct = default)
    {
        if (!await roles.EliminarAsync(await Actor("configuracion.gestionar", ct), id, ct))
            throw new AccesoNoPermitidoException();
    }

    public async Task ActualizarPermisosAsync(Guid id, IReadOnlyList<string> codes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Invalid permission code.");
        if (!await roles.ActualizarPermisosAsync(await Actor("configuracion.gestionar", ct), id, codes, ct))
            throw new AccesoNoPermitidoException();
    }

    public async Task AsignarAsync(Guid accessId, Guid? roleId, CancellationToken ct = default)
    {
        var actor = await Actor("usuarios.gestionar", ct);
        var puedeAsignarProtegido = await permissions.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, "configuracion.gestionar", ct);
        if (!await roles.AsignarAsync(actor, accessId, roleId, puedeAsignarProtegido, ct))
            throw new AccesoNoPermitidoException();
    }
}
