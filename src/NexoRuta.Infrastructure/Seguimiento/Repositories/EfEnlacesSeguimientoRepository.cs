using Microsoft.EntityFrameworkCore;

using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.Dtos;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Seguimiento.Repositories;

public sealed class EfEnlacesSeguimientoRepository(
    NexoRutaDbContext db
) : IEnlacesSeguimientoRepository
{
    public async Task GuardarAsync(
        EnlaceSeguimiento enlace,
        CancellationToken cancellationToken = default)
    {
        db.EnlacesSeguimiento.Add(enlace);

        await db.SaveChangesAsync(cancellationToken);
    }


    public async Task<EnlaceSeguimiento?> BuscarPorHashAsync(
    string tokenHash,
    CancellationToken cancellationToken = default)
    {
        return await db.EnlacesSeguimiento
            .AsNoTracking()
            .SingleOrDefaultAsync(
                enlace => enlace.TokenHash == tokenHash,
                cancellationToken);
    }

    public async Task<EstadoEnvio?> ObtenerEstadoPublicoAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var result = await db.Database.SqlQueryRaw<PublicTrackingState>(
            "SELECT \"Estado\" FROM " + RlsSqlFunctionContract.PublicTracking + "({0})", token)
            .SingleOrDefaultAsync(cancellationToken);
        return result is not null && Enum.TryParse<EstadoEnvio>(result.Estado, out var state) ? state : null;
    }

    public async Task<SeguimientoPublico?> ObtenerSeguimientoPublicoAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var result = await db.Database.SqlQueryRaw<PublicTrackingIdentity>(
            "SELECT * FROM " + RlsSqlFunctionContract.PublicTracking + "({0})", token)
            .SingleOrDefaultAsync(cancellationToken);
        if (result is null || !Enum.TryParse<EstadoEnvio>(result.Estado, out var state)) return null;
        return new SeguimientoPublico(state, new IdentidadVisualPublica(result.NombreOperador,
            result.CorreoPublico, result.TelefonoPublico, result.ColorPrimario, result.ColorSecundario,
            result.LogoContenido, result.LogoTipoContenido));
    }

    private sealed record PublicTrackingState(string Estado);
    private sealed record PublicTrackingIdentity(string Estado, string NombreOperador, string? CorreoPublico,
        string? TelefonoPublico, string? ColorPrimario, string? ColorSecundario, byte[]? LogoContenido,
        string? LogoTipoContenido);
}
