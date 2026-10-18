
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class EfUsuariosLoginRepository(
    NexoRutaDbContext db) : IUsuariosLoginRepository
{
    public async Task<UsuarioLogin?> ObtenerPorEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var emailNormalizado = email.Trim().ToLowerInvariant();
        var row = await db.Database.SqlQuery<LoginLookupRow>($"""
            SELECT * FROM nexoruta_app.login_lookup({emailNormalizado})
            """).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        return new UsuarioLogin(
            row.UsuarioId,
            row.AccesoId,
            row.Email,
            row.VersionSesion,
            row.EsPlataforma,
            Enum.TryParse<TipoAccesoUsuario>(row.TipoOrganizacion, out var tipo) ? tipo : null,
            Enum.TryParse<EstadoOrganizacion>(row.EstadoOrganizacion, out var estado) ? estado : null);
    }

    private sealed record LoginLookupRow(Guid UsuarioId, Guid? AccesoId, string Email, long VersionSesion,
        bool EsPlataforma, string? TipoOrganizacion, string? EstadoOrganizacion);
}
