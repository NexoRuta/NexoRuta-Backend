using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Caching;
using NexoRuta.Infrastructure.Hosting;
using NexoRuta.Infrastructure.IdentidadVisual;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion;

public sealed class EfConsultaIdentidadVisualOperador(
    NexoRutaDbContext db,
    DistributedCacheAccess cache) : IConsultaIdentidadVisualOperador
{
    public async Task<IdentidadVisualOperadorSnapshot?> ObtenerVigenteAsync(Guid operadorId,
        CancellationToken cancellationToken = default)
    {
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        var version = await db.VersionesIdentidadVisualOperador.AsNoTracking()
            .Where(item => item.OperadorId == operadorId)
            .MaxAsync(item => (int?)item.Version, cancellationToken);
        if (version is null) return null;

        var key = cache.Key(DistributedCacheTelemetry.OperatorBranding, $"{operadorId:D}:{version.Value}");
        var cached = await cache.GetAsync<IdentidadVisualOperadorSnapshot>(
            DistributedCacheTelemetry.OperatorBranding, key,
            value => value.OperadorId == operadorId && value.Version == version.Value
                && value.Version > 0 && ((value.LogoContenido is null && value.LogoTipoContenido is null)
                    || (value.LogoContenido is { Length: > 0 and <= ValidadorIdentidadVisual.MaximoLogoBytes }
                        && value.LogoTipoContenido is "image/png" or "image/jpeg")), cancellationToken);
        if (cached.Lookup == DistributedCacheLookup.Hit) return cached.Value;

        var snapshot = await db.VersionesIdentidadVisualOperador.AsNoTracking()
            .Where(item => item.OperadorId == operadorId && item.Version == version.Value)
            .Select(item => new IdentidadVisualOperadorSnapshot(item.OperadorId, item.Version,
                item.NombreComercial, item.CorreoPublico, item.TelefonoPublico, item.ColorPrimario,
                item.ColorSecundario, item.LogoContenido, item.LogoTipoContenido))
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot is not null)
            await cache.SetAsync(DistributedCacheTelemetry.OperatorBranding, key, snapshot,
                cacheSettings.OperatorBranding.TtlSeconds, cancellationToken);
        return snapshot;
    }

    private DistributedCacheSettings cacheSettings => cache.Settings;
}
