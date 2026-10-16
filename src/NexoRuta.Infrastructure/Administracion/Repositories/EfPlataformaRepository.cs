using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfPlataformaRepository(NexoRutaDbContext db, IGeneradorPasswordHash hasher) : IPlataformaRepository, IAltaPropiaRepository
{
    public Task<ContextoPlataforma?> ObtenerAccesoAsync(Guid accesoId, CancellationToken ct)
        => (from access in db.AccesosPlataforma.AsNoTracking()
            join user in db.Usuarios on access.UsuarioId equals user.Id
            join role in db.RolesPlataforma on access.RolId equals role.Id
            where access.AccesoId == accesoId && access.Activo && user.PasswordHash != null && user.PasswordHash != ""
            select new ContextoPlataforma(user.Id, access.AccesoId, access.VersionSesion,
                role.PuedeGestionarOrganizaciones &&
                    (from grant in db.RolesPlataformaPermisos join permission in db.Permisos on grant.PermisoId equals permission.Id
                     where grant.RolId == role.Id && permission.Codigo == RolPlataforma.GestionarOrganizaciones select grant).Any(),
                user.Email)).SingleOrDefaultAsync(ct);

    public async Task InicializarAsync(string email, string password, CancellationToken ct = default, bool permitirExistente = false)
    {
        var normalized = ValidacionCredenciales.Validar(email, password);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // ponytail: one initial account is supported; a transaction-scoped table lock serializes bootstrap only.
        // Multi-account provisioning needs a scoped lock and an explicit administration workflow.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AccesosPlataforma\" IN SHARE ROW EXCLUSIVE MODE", ct);
        var existing = await (from access in db.AccesosPlataforma.AsNoTracking()
                              join account in db.Usuarios on access.UsuarioId equals account.Id
                              join platformRole in db.RolesPlataforma on access.RolId equals platformRole.Id
                              select new { access, account, platformRole }).SingleOrDefaultAsync(ct);
        var codes = CatalogoPermisos.Plataforma.Select(x => x.Codigo).ToArray();
        if (existing is not null)
        {
            if (!permitirExistente)
                throw new InvalidOperationException("Platform initialization has already completed; no account was changed.");
            var grants = await (from grant in db.RolesPlataformaPermisos
                                join permission in db.Permisos on grant.PermisoId equals permission.Id
                                where grant.RolId == existing.platformRole.Id select permission.Codigo).ToArrayAsync(ct);
            if (existing.account.Email.Trim().ToLowerInvariant() != normalized || string.IsNullOrWhiteSpace(existing.account.PasswordHash) ||
                !existing.access.Activo || !existing.platformRole.EsProtegidoSistema || !existing.platformRole.PuedeGestionarOrganizaciones ||
                !grants.ToHashSet(StringComparer.Ordinal).SetEquals(codes) ||
                await db.AccesosUsuario.AnyAsync(x => x.UsuarioId == existing.account.Id, ct))
                throw new InvalidOperationException("Existing platform account is incompatible with configured initialization (identity, state or permissions); no account was changed.");
            await transaction.CommitAsync(ct);
            return;
        }
        if (await EmailExists(normalized, ct)) throw new CorreoYaRegistradoException();
        var user = new Usuario(normalized, hasher.Generar(normalized, password));
        var role = new RolPlataforma();
        db.AddRange(user, role, new AccesoPlataforma(user, role));
        try
        {
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

    public Task<AltaOrganizacion> CrearAsync(TipoAccesoUsuario tipo, string nombre, string email, string password, CancellationToken ct)
    {
        var normalized = ValidacionCredenciales.Validar(email, password);
        return CrearOrganizacionAsync(tipo, nombre, new Usuario(normalized, hasher.Generar(normalized, password)), true, ct);
    }

    public async Task<AltaOrganizacion> CrearPropiaAsync(Guid usuarioId, TipoAccesoUsuario tipo, string nombre, CancellationToken ct)
    {
        var user = await db.Usuarios.SingleOrDefaultAsync(x => x.Id == usuarioId, ct)
            ?? throw new ArgumentException("La identidad no existe.");
        return await CrearOrganizacionAsync(tipo, nombre, user, false, ct);
    }

    private async Task<AltaOrganizacion> CrearOrganizacionAsync(TipoAccesoUsuario tipo, string nombre, Usuario user, bool nuevaIdentidad, CancellationToken ct)
    {
        if (tipo is not (TipoAccesoUsuario.Operador or TipoAccesoUsuario.Comercio))
            throw new ArgumentException("Invalid organization type.", nameof(tipo));
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 160)
            throw new ArgumentException("An organization name of at most 160 characters is required.", nameof(nombre));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (nuevaIdentidad)
        {
            if (await EmailExists(user.Email, ct)) throw new CorreoYaRegistradoException();
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
        var access = new AccesoUsuario(user.Id, operatorEntity?.Id, commerceEntity?.Id, role);
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
            db.AddRange(organization, role, access);
            if (nuevaIdentidad) db.Add(user);
            db.Entry(organization).Property("BootstrapCompletado").CurrentValue = true;
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

    public async Task<PaginaOrganizaciones> ListarAsync(TipoAccesoUsuario tipo, int pagina, int tamanoPagina, CancellationToken ct)
    {
        if (pagina < 1 || tamanoPagina is < 1 or > 100 || pagina > int.MaxValue / tamanoPagina)
            throw new ArgumentException("Pagina must be positive and tamanoPagina must be between 1 and 100.");
        var query = Consulta(tipo);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Nombre).ThenBy(x => x.Id)
            .Skip((pagina - 1) * tamanoPagina).Take(tamanoPagina).ToListAsync(ct);
        return new(items, pagina, tamanoPagina, total);
    }

    public Task<OrganizacionAdministrativa?> ObtenerAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => Consulta(tipo).SingleOrDefaultAsync(x => x.Id == id, ct);

    private IQueryable<OrganizacionAdministrativa> Consulta(TipoAccesoUsuario tipo)
        => tipo == TipoAccesoUsuario.Operador
            ? db.Operadores.AsNoTracking().Select(x => new OrganizacionAdministrativa { Id = x.Id, Nombre = x.Nombre })
            : db.Comercios.AsNoTracking().Select(x => new OrganizacionAdministrativa { Id = x.Id, Nombre = x.Nombre });

    private Task<bool> EmailExists(string email, CancellationToken ct)
        => db.Usuarios.AnyAsync(x => x.Email.Trim().ToLower() == email, ct);

    private static bool IsUniqueViolation(DbUpdateException error)
        => error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
