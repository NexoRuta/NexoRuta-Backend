using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class DatosInicialesSeeder(NexoRutaDbContext db, AccesoInicial initial,
    IConfiguration configuration, IPasswordHasher<string> passwordHasher)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (initial.OperadorId is null || initial.ComercioId is null || initial.OperadorId == Guid.Empty || initial.ComercioId == Guid.Empty)
            throw new InvalidOperationException("Bootstrap requires persistent organization IDs.");
        if (string.Equals(initial.UsuarioEmail, initial.OperadorUsuarioEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bootstrap accounts must be distinct.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Operadores.AnyAsync(x => x.Id == initial.OperadorId, cancellationToken))
            db.Add(new Operador(initial.OperadorNombre, initial.OperadorId));
        if (!await db.Comercios.AnyAsync(x => x.Id == initial.ComercioId, cancellationToken))
            db.Add(new Comercio(initial.ComercioNombre, initial.ComercioId));
        await db.SaveChangesAsync(cancellationToken);

        var operatorEntity = await db.Operadores.SingleAsync(x => x.Id == initial.OperadorId, cancellationToken);
        var commerceEntity = await db.Comercios.SingleAsync(x => x.Id == initial.ComercioId, cancellationToken);
        var operatorCompleted = db.Entry(operatorEntity).Property<bool>("BootstrapCompletado").CurrentValue;
        var commerceCompleted = db.Entry(commerceEntity).Property<bool>("BootstrapCompletado").CurrentValue;
        // Stable endpoint permission codes are technical contracts, not a role/grant catalog.
        foreach (var code in new[] { "usuarios.gestionar", "configuracion.gestionar", "envios.crear", "envios.consultar", "envios.gestionar" })
            if (!await db.Permisos.AnyAsync(x => x.Codigo == code, cancellationToken)) db.Add(new Permiso(code, code));
        foreach (var definition in configuration.GetSection("Bootstrap:Roles").Get<RolInicial[]>() ?? [])
        {
            var role = new Rol(definition.Codigo, definition.Nombre, definition.OperadorId, definition.ComercioId);
            if (role.OperadorId != initial.OperadorId && role.ComercioId != initial.ComercioId)
                throw new InvalidOperationException("Bootstrap roles must belong to the configured organizations.");
            if (role.OperadorId.HasValue ? operatorCompleted : commerceCompleted) continue;
            if (await db.Roles.AnyAsync(x => x.Codigo == role.Codigo && x.OrganizacionId == role.OrganizacionId
                && x.TipoOrganizacion == role.TipoOrganizacion, cancellationToken)) continue;
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
        await EnsureAccount(initial.OperadorUsuarioEmail, initial.OperadorId, null, initial.OperadorRolCodigo,
            "AccesoInicial:OperadorUsuarioPassword", cancellationToken);
        await EnsureAccount(initial.UsuarioEmail, null, initial.ComercioId, initial.ComercioRolCodigo,
            "AccesoInicial:UsuarioPassword", cancellationToken);
        db.Entry(operatorEntity).Property<bool>("BootstrapCompletado").CurrentValue = true;
        db.Entry(commerceEntity).Property<bool>("BootstrapCompletado").CurrentValue = true;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureAccount(string email, Guid? operatorId, Guid? commerceId, string? code,
        string passwordSetting, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = await db.Usuarios.SingleOrDefaultAsync(x => x.Email.ToLower() == normalized, ct);
        var isNew = user is null;
        if (isNew)
        {
            var password = configuration[passwordSetting];
            if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
                throw new InvalidOperationException($"A bootstrap password of at least 12 characters is required: {passwordSetting}.");
            user = new Usuario(normalized, passwordHasher.HashPassword(normalized, password));
            db.Add(user);
        }
        if (await db.AccesosUsuario.AnyAsync(x => x.UsuarioId == user!.Id, ct)) return;
        // ponytail: bootstrap grants are create-only; repair of an existing user requires explicit administrative assignment.
        var role = isNew && !string.IsNullOrWhiteSpace(code)
            ? await db.Roles.SingleOrDefaultAsync(x => x.Codigo == code && x.OperadorId == operatorId && x.ComercioId == commerceId, ct)
            : null;
        if (isNew && !string.IsNullOrWhiteSpace(code) && role is null)
            throw new InvalidOperationException("The configured initial role does not belong to this organization.");
        db.Add(new AccesoUsuario(user!.Id, operatorId, commerceId, role));
    }
}
