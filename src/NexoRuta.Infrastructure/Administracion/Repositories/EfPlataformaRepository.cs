using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfPlataformaRepository(NexoRutaDbContext db, UserManager<Usuario> identityUsers) : IPlataformaRepository, IAltaPropiaRepository
{
    public async Task<ContextoPlataforma?> ObtenerAccesoAsync(Guid accesoId, CancellationToken ct)
    {
        var row = await db.Database.SqlQueryRaw<PlatformAccessLookupRow>(
            "SELECT * FROM " + RlsSqlFunctionContract.LookupPlatformAccess + "({0})", accesoId)
            .SingleOrDefaultAsync(ct);
        return row is null ? null : new(row.UsuarioId, row.AccesoId, row.VersionSesion,
            row.PuedeGestionarOrganizaciones, row.UsuarioEmail);
    }

    public async Task InicializarAsync(string email, string password, CancellationToken ct = default, bool sincronizarPasswordExistente = false)
    {
        var normalized = ValidacionCredenciales.Validar(email, password);
        await using var transaction = await db.BeginOrJoinAsync(ct);
        await db.ApplyAsync(RlsActorContext.Bootstrap(null, null), ct);
        // ponytail: one initial account is supported; a transaction advisory lock serializes bootstrap only.
        // Multi-account provisioning needs a scoped lock and an explicit administration workflow.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('nexoruta:platform-bootstrap', 0))", ct);
        var existing = await (from access in db.AccesosPlataforma.AsNoTracking()
                              join account in db.Usuarios.AsNoTracking() on access.UsuarioId equals account.Id
                              join platformRole in db.RolesPlataforma.AsNoTracking() on access.RolId equals platformRole.Id
                              select new
                              {
                                  access,
                                  accountId = account.Id,
                                  accountEmail = account.Email,
                                  passwordHash = account.PasswordHash,
                                  platformRole
                              }).SingleOrDefaultAsync(ct);
        var codes = CatalogoPermisos.Plataforma.Select(x => x.Codigo).ToArray();
        if (existing is not null)
        {
            if (!sincronizarPasswordExistente)
                throw new InvalidOperationException("Platform initialization has already completed; no account was changed.");
            var grants = await (from grant in db.RolesPlataformaPermisos
                                join permission in db.Permisos on grant.PermisoId equals permission.Id
                                where grant.RolId == existing.platformRole.Id select permission.Codigo).ToArrayAsync(ct);
            if (existing.accountEmail!.Trim().ToLowerInvariant() != normalized || string.IsNullOrWhiteSpace(existing.passwordHash) ||
                !existing.access.Activo || !existing.platformRole.EsProtegidoSistema || !existing.platformRole.PuedeGestionarOrganizaciones ||
                !grants.ToHashSet(StringComparer.Ordinal).SetEquals(codes) ||
                await db.AccesosUsuario.AnyAsync(x => x.UsuarioId == existing.accountId, ct))
                throw new InvalidOperationException("Existing platform account is incompatible with configured initialization (identity, state or permissions); no account was changed.");
            var account = await db.Usuarios.SingleAsync(x => x.Id == existing.accountId, ct);
            if (account.Email!.Trim().ToLowerInvariant() != normalized || string.IsNullOrWhiteSpace(account.PasswordHash))
                throw new InvalidOperationException("Existing platform account is incompatible with configured initialization (identity or password); no account was changed.");
            await SincronizarPasswordConfiguradaAsync(account, password, ct);
            await transaction.CommitAsync(ct);
            return;
        }
        if (await EmailExists(normalized, ct)) throw new CorreoYaRegistradoException();
        try
        {
            var user = new Usuario(normalized);
            var userResult = await identityUsers.CreateAsync(user, password);
            IdentityErrorContract.ExigirCreacionValida(userResult, identityUsers.ErrorDescriber);
            var role = new RolPlataforma();
            db.AddRange(role, new AccesoPlataforma(user, role));
            foreach (var definition in CatalogoPermisos.Plataforma)
            {
                var permission = new Permiso(definition.Codigo, definition.Nombre);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Permisos" ("Id", "Codigo", "Nombre") VALUES ({permission.Id}, {permission.Codigo}, {permission.Nombre})
                    ON CONFLICT ("Codigo") DO NOTHING
                    """, ct);
            }
            var permissions = await db.Permisos.Where(x => codes.Contains(x.Codigo)).ToListAsync(ct);
            db.AddRange(permissions.Select(x => new RolPlataformaPermiso(role.Id, x.Id)));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            throw new InvalidOperationException("Platform initialization conflicts with an existing account or initialization; no account was changed.");
        }
    }

    private async Task SincronizarPasswordConfiguradaAsync(Usuario account, string password, CancellationToken ct)
    {
        var verification = identityUsers.PasswordHasher.VerifyHashedPassword(account, account.PasswordHash!, password);
        if (verification == PasswordVerificationResult.Success) return;

        var passwordChanged = verification == PasswordVerificationResult.Failed;
        if (passwordChanged)
        {
            foreach (var validator in identityUsers.PasswordValidators)
            {
                var validation = await validator.ValidateAsync(identityUsers, account, password);
                if (!validation.Succeeded)
                    throw new InvalidOperationException("Configured platform password could not be synchronized; initialization was rolled back.");
            }
        }

        account.ActualizarPasswordHash(identityUsers.PasswordHasher.HashPassword(account, password));
        account.ConcurrencyStamp = Guid.NewGuid().ToString();
        if (passwordChanged) account.RevocarSesionesPorCambioDePassword();
        await db.SaveChangesAsync(ct);
    }

    public Task<AltaOrganizacion> CrearAsync(TipoAccesoUsuario tipo, string nombre, string email, string password, CancellationToken ct)
    {
        var normalized = ValidacionCredenciales.Validar(email, password);
        return CrearOrganizacionAsync(tipo, nombre, new Usuario(normalized), true, password, ct);
    }

    public async Task<AltaOrganizacion> CrearPropiaAsync(Guid usuarioId, TipoAccesoUsuario tipo, string nombre, CancellationToken ct)
    {
        var user = await db.Usuarios.SingleOrDefaultAsync(x => x.Id == usuarioId, ct)
            ?? throw new ArgumentException("La identidad no existe.");
        return await CrearOrganizacionAsync(tipo, nombre, user, false, null, ct);
    }

    private async Task<AltaOrganizacion> CrearOrganizacionAsync(TipoAccesoUsuario tipo, string nombre, Usuario user, bool nuevaIdentidad, string? password, CancellationToken ct)
    {
        if (tipo is not (TipoAccesoUsuario.Operador or TipoAccesoUsuario.Comercio))
            throw new ArgumentException("Invalid organization type.", nameof(tipo));
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 160)
            throw new ArgumentException("An organization name of at most 160 characters is required.", nameof(nombre));
        await using var transaction = await db.BeginOrJoinAsync(ct);
        if (nuevaIdentidad)
        {
            if (await EmailExists(user.Email!, ct)) throw new CorreoYaRegistradoException();
        }
        else if (await db.AccesosUsuario.AnyAsync(x => x.UsuarioId == user.Id, ct) ||
                 await db.AccesosPlataforma.AnyAsync(x => x.UsuarioId == user.Id, ct))
            throw new OrganizacionYaAsignadaException();
        var operatorEntity = tipo == TipoAccesoUsuario.Operador ? new Operador(nombre) : null;
        var commerceEntity = tipo == TipoAccesoUsuario.Comercio ? new Comercio(nombre) : null;
        object organization = (object?)operatorEntity ?? commerceEntity!;
        var id = operatorEntity?.Id ?? commerceEntity!.Id;
        var role = new Rol(nuevaIdentidad ? "administrador" : "dueno", nuevaIdentidad ? "Administrator" : "Owner", operatorEntity?.Id, commerceEntity?.Id);
        role.ProtegerComoRolSistema();
        var access = new AccesoUsuario(user.Id, operatorEntity?.Id, commerceEntity?.Id, role, esPropietario: true);
        await db.SetOrganizationCreationAsync(id, user.Id, role.Id, ct);
        try
        {
            // Catalog upserts and every organization row belong to the same transaction.
            foreach (var definition in CatalogoPermisos.Para(tipo))
            {
                var permission = new Permiso(definition.Codigo, definition.Nombre);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Permisos" ("Id", "Codigo", "Nombre")
                    VALUES ({permission.Id}, {permission.Codigo}, {permission.Nombre})
                    ON CONFLICT ("Codigo") DO NOTHING
                    """, ct);
            }
            var codes = CatalogoPermisos.Para(tipo).Select(x => x.Codigo).ToArray();
            var permissions = await db.Permisos.Where(x => codes.Contains(x.Codigo)).ToListAsync(ct);
            db.Entry(organization).Property("BootstrapCompletado").CurrentValue = true;
            if (operatorEntity is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Operadores" ("Id", "Nombre", "Estado", "Version", "BootstrapCompletado")
                    VALUES ({operatorEntity.Id}, {operatorEntity.Nombre}, 'Activa', 0, true)
                    """, ct);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Comercios" ("Id", "Nombre", "Estado", "Version", "BootstrapCompletado")
                    VALUES ({commerceEntity!.Id}, {commerceEntity.Nombre}, 'Activa', 0, true)
                    """, ct);
            db.Entry(organization).State = EntityState.Unchanged;
            if (nuevaIdentidad)
            {
                var userResult = await identityUsers.CreateAsync(user, password!);
                IdentityErrorContract.ExigirCreacionValida(userResult, identityUsers.ErrorDescriber);
            }
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
            db.AccesosUsuario.Add(access);
            db.AddRange(permissions.Select(p => new RolPermiso(role.Id, p.Id)));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            if (nuevaIdentidad) throw new CorreoYaRegistradoException();
            throw new OrganizacionYaAsignadaException();
        }
        return new AltaOrganizacion(new() { Id = id, Nombre = nombre.Trim() }, user.Id, access.Id, role.Id);
    }

    public async Task<PaginaOrganizaciones> ListarAsync(TipoAccesoUsuario tipo, EstadoOrganizacion? estado, int pagina, int tamanoPagina, CancellationToken ct)
    {
        if (pagina < 1 || tamanoPagina is < 1 or > 100 || pagina > int.MaxValue / tamanoPagina)
            throw new ArgumentException("Pagina must be positive and tamanoPagina must be between 1 and 100.");
        var query = Consulta(tipo);
        if (estado is not null)
            query = query.Where(x => x.Estado == estado.Value.ToString());
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Nombre).ThenBy(x => x.Id)
            .Skip((pagina - 1) * tamanoPagina).Take(tamanoPagina).ToListAsync(ct);
        return new(items, pagina, tamanoPagina, total);
    }

    public Task<OrganizacionAdministrativa?> ObtenerAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => Consulta(tipo).SingleOrDefaultAsync(x => x.Id == id, ct);

    private IQueryable<OrganizacionAdministrativa> Consulta(TipoAccesoUsuario tipo)
        => tipo == TipoAccesoUsuario.Operador
            ? db.Operadores.AsNoTracking().Select(x => new OrganizacionAdministrativa { Id = x.Id, Nombre = x.Nombre, Estado = x.Estado.ToString(), Version = x.Version })
            : db.Comercios.AsNoTracking().Select(x => new OrganizacionAdministrativa { Id = x.Id, Nombre = x.Nombre, Estado = x.Estado.ToString(), Version = x.Version });

    private Task<bool> EmailExists(string email, CancellationToken ct)
        => db.Usuarios.AnyAsync(x => x.Email!.Trim().ToLower() == email, ct);

    private sealed record PlatformAccessLookupRow(Guid UsuarioId, Guid AccesoId, long VersionSesion,
        bool PuedeGestionarOrganizaciones, string UsuarioEmail);

    private static bool IsUniqueViolation(DbUpdateException error)
        => error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
