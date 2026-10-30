using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Administracion.Exceptions;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class DatosInicialesSeeder(NexoRutaDbContext db, AccesoInicial initial,
    IConfiguration configuration, UserManager<Usuario> identityUsers, IPlataformaRepository plataforma)
{
    public async Task SeedPlataformaAsync(CancellationToken ct = default)
    {
        if (!configuration.GetValue<bool>("PlatformInitialization:Enabled")) return;
        var email = configuration["PlatformInitialization:Email"];
        var password = configuration["PlatformInitialization:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Platform initialization requires PlatformInitialization:Email and PlatformInitialization:Password; no account was changed.");
        try
        {
            await plataforma.InicializarAsync(email, password, ct, sincronizarPasswordExistente: true);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Platform initialization requires a valid email and a password of at least 12 characters; no account was changed.");
        }
        catch (CorreoYaRegistradoException)
        {
            throw new InvalidOperationException("Platform initialization email belongs to an existing identity; it will not be elevated or modified.");
        }
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (initial.OperadorId is null || initial.ComercioId is null || initial.OperadorId == Guid.Empty || initial.ComercioId == Guid.Empty)
            throw new InvalidOperationException("Bootstrap requires persistent organization IDs.");
        if (string.Equals(initial.UsuarioEmail, initial.OperadorUsuarioEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bootstrap accounts must be distinct.");
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        await db.ApplyAsync(RlsActorContext.Bootstrap(initial.OperadorId, initial.ComercioId), cancellationToken);
        if (!await db.Operadores.AnyAsync(x => x.Id == initial.OperadorId, cancellationToken))
            db.Add(new Operador(initial.OperadorNombre, initial.OperadorId));
        if (!await db.Comercios.AnyAsync(x => x.Id == initial.ComercioId, cancellationToken))
            db.Add(new Comercio(initial.ComercioNombre, initial.ComercioId));
        await db.SaveChangesAsync(cancellationToken);

        var operatorEntity = await db.Operadores.SingleAsync(x => x.Id == initial.OperadorId, cancellationToken);
        var commerceEntity = await db.Comercios.SingleAsync(x => x.Id == initial.ComercioId, cancellationToken);
        var operatorCompleted = db.Entry(operatorEntity).Property<bool>("BootstrapCompletado").CurrentValue;
        var commerceCompleted = db.Entry(commerceEntity).Property<bool>("BootstrapCompletado").CurrentValue;
        foreach (var permiso in CatalogoPermisos.Todos)
            if (!await db.Permisos.AnyAsync(x => x.Codigo == permiso.Codigo, cancellationToken))
                db.Add(new Permiso(permiso.Codigo, permiso.Nombre));
        var definitions = configuration.GetSection("Bootstrap:Roles").Get<RolInicial[]>() ?? [];
        foreach (var definition in definitions)
        {
            var role = new Rol(definition.Codigo, definition.Nombre, definition.OperadorId, definition.ComercioId);
            if (role.OperadorId != initial.OperadorId && role.ComercioId != initial.ComercioId)
                throw new InvalidOperationException("Bootstrap roles must belong to the configured organizations.");
            if (definition.Permisos.Any(code => !CatalogoPermisos.EsAplicable(code, role.TipoOrganizacion)))
                throw new InvalidOperationException("Bootstrap roles can only receive registered permissions applicable to their organization.");
            var isConfiguredOwnerRole =
                (role.OperadorId == initial.OperadorId && role.Codigo == initial.OperadorRolCodigo)
                || (role.ComercioId == initial.ComercioId && role.Codigo == initial.ComercioRolCodigo);
            var existing = await db.Roles.SingleOrDefaultAsync(x => x.Codigo == role.Codigo
                && x.OrganizacionId == role.OrganizacionId && x.TipoOrganizacion == role.TipoOrganizacion, cancellationToken);
            if (existing is not null)
            {
                if (isConfiguredOwnerRole) existing.ProtegerComoRolSistema();
                continue;
            }
            if (role.OperadorId.HasValue ? operatorCompleted : commerceCompleted) continue;
            if (isConfiguredOwnerRole) role.ProtegerComoRolSistema();
            db.Add(role);
            foreach (var code in definition.Permisos.Distinct(StringComparer.Ordinal))
            {
                var permission = db.Permisos.Local.SingleOrDefault(x => x.Codigo == code)
                    ?? await db.Permisos.SingleOrDefaultAsync(x => x.Codigo == code, cancellationToken);
                if (permission is null)
                {
                    permission = new Permiso(code, code);
                    db.Add(permission);
                }
                db.Add(new RolPermiso(role.Id, permission.Id));
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await EnsureDispatcherRoleAsync(initial.OperadorId.Value, cancellationToken);
        await EnsureOwnerRolePermissionsAsync(TipoAccesoUsuario.Operador, initial.OperadorId.Value,
            initial.OperadorRolCodigo, definitions, cancellationToken);
        await EnsureOwnerRolePermissionsAsync(TipoAccesoUsuario.Comercio, initial.ComercioId.Value,
            initial.ComercioRolCodigo, definitions, cancellationToken);
        await EnsureAccount(initial.OperadorUsuarioEmail, initial.OperadorId, null, initial.OperadorRolCodigo,
            "AccesoInicial:OperadorUsuarioPassword", cancellationToken);
        await EnsureAccount(initial.UsuarioEmail, null, initial.ComercioId, initial.ComercioRolCodigo,
            "AccesoInicial:UsuarioPassword", cancellationToken);
        db.Entry(operatorEntity).Property<bool>("BootstrapCompletado").CurrentValue = true;
        db.Entry(commerceEntity).Property<bool>("BootstrapCompletado").CurrentValue = true;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureDispatcherRoleAsync(Guid operatorId, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(x => x.OperadorId == operatorId
            && x.Codigo == CodigosRolesSistema.DespachadorOperador, ct);
        if (role is null)
        {
            role = new Rol(CodigosRolesSistema.DespachadorOperador, "Dispatcher", operatorId, null);
            role.ProtegerComoRolSistema();
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
        }
        else if (!role.EsProtegidoSistema)
        {
            throw new InvalidOperationException("The reserved dispatcher role code is already used by an unprotected role.");
        }

        var permissions = await db.Permisos.Where(x => x.Codigo == "envios.gestionar" || x.Codigo == "envios.consultar")
            .ToListAsync(ct);
        if (permissions.Count != 2)
            throw new InvalidOperationException("The registered shipment-management and consultation permissions are missing.");
        var assigned = await db.RolesPermisos.Where(x => x.RolId == role.Id)
            .Select(x => x.PermisoId).ToHashSetAsync(ct);
        foreach (var permission in permissions.Where(x => !assigned.Contains(x.Id)))
            db.RolesPermisos.Add(new RolPermiso(role.Id, permission.Id));
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    private async Task EnsureOwnerRolePermissionsAsync(
        TipoAccesoUsuario scope, Guid organizationId, string? roleCode,
        IReadOnlyList<RolInicial> definitions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roleCode)) return;
        var operatorId = scope == TipoAccesoUsuario.Operador ? organizationId : (Guid?)null;
        var commerceId = scope == TipoAccesoUsuario.Comercio ? organizationId : (Guid?)null;
        var definition = definitions.SingleOrDefault(x => x.Codigo == roleCode
            && x.OperadorId == operatorId && x.ComercioId == commerceId)
            ?? throw new InvalidOperationException("The configured organization owner must have a scoped bootstrap role definition.");

        var role = await db.Roles.SingleOrDefaultAsync(x => x.Codigo == roleCode
            && x.OrganizacionId == organizationId && x.TipoOrganizacion == scope, ct);
        if (role is null)
            throw new InvalidOperationException("The configured organization owner role is missing after bootstrap.");
        role.ProtegerComoRolSistema();

        // The centralized permission catalog is the source of truth for protected organization-owner grants.
        var codes = CatalogoPermisos.Para(scope).Select(x => x.Codigo).ToArray();
        var permissions = new List<Permiso>(codes.Length);
        foreach (var code in codes)
        {
            var permission = db.Permisos.Local.SingleOrDefault(x => x.Codigo == code)
                ?? await db.Permisos.SingleOrDefaultAsync(x => x.Codigo == code, ct);
            if (permission is null)
                throw new InvalidOperationException($"Registered permission {code} is missing after bootstrap.");
            permissions.Add(permission);
        }

        var grants = await db.RolesPermisos.Where(x => x.RolId == role.Id).ToListAsync(ct);
        db.RemoveRange(grants.Where(x => !permissions.Any(p => p.Id == x.PermisoId)));
        foreach (var permission in permissions.Where(p => !grants.Any(x => x.PermisoId == p.Id)))
            db.Add(new RolPermiso(role.Id, permission.Id));
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureAccount(string email, Guid? operatorId, Guid? commerceId, string? code,
        string passwordSetting, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = await db.Usuarios.SingleOrDefaultAsync(x => x.Email!.ToLower() == normalized, ct);
        var isNew = user is null;
        if (isNew)
        {
            var password = configuration[passwordSetting];
            if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
                throw new InvalidOperationException($"A bootstrap password of at least 12 characters is required: {passwordSetting}.");
            user = new Usuario(normalized);
            var result = await identityUsers.CreateAsync(user, password);
            if (!result.Succeeded)
                throw new InvalidOperationException("The configured bootstrap identity was rejected by Identity.");
        }
        await db.SetBootstrapUserAsync(user!.Id, ct);
        if (await db.AccesosUsuario.AnyAsync(x => x.UsuarioId == user!.Id, ct)) return;
        // ponytail: bootstrap grants are create-only; repair of an existing user requires explicit administrative assignment.
        var role = isNew && !string.IsNullOrWhiteSpace(code)
            ? await db.Roles.SingleOrDefaultAsync(x => x.Codigo == code && x.OperadorId == operatorId && x.ComercioId == commerceId, ct)
            : null;
        if (isNew && !string.IsNullOrWhiteSpace(code) && role is null)
            throw new InvalidOperationException("The configured initial role does not belong to this organization.");
        db.Add(new AccesoUsuario(user!.Id, operatorId, commerceId, role, esPropietario: role is not null));
    }
}
