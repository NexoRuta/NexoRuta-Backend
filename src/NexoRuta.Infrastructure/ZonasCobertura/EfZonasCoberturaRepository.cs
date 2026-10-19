using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using Npgsql;
using NexoRuta.Application.ZonasCobertura.Repositories;
using NexoRuta.Application.ZonasCobertura;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Persistence;
using NetTopologySuite;

namespace NexoRuta.Infrastructure.ZonasCobertura;

public sealed class EfZonasCoberturaRepository : IZonasCoberturaRepository
{
    private const string TerritoryCacheKeyPrefix = "NexoRuta.CU02.UruguayTerritory";
    private static readonly SemaphoreSlim TerritoryCacheGate = new(1, 1);
    private readonly NexoRutaDbContext db;
    private readonly IMemoryCache cache;

    public EfZonasCoberturaRepository(NexoRutaDbContext db, IMemoryCache? cache = null)
    {
        this.db = db;
        this.cache = cache ?? new MemoryCache(new MemoryCacheOptions());
    }

    public async Task<(IReadOnlyList<ZonaCoberturaResumen> Items, int Total)> ListarAsync(
        EstadoZonaCobertura? estado, int pagina, int tamanoPagina, CancellationToken cancellationToken = default)
    {
        var query = db.ZonasCobertura.AsNoTracking();
        if (estado is not null) query = query.Where(x => x.Estado == estado.Value);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.Id)
            .Skip(checked((pagina - 1) * tamanoPagina))
            .Take(tamanoPagina)
            .Select(x => new ZonaCoberturaResumen(x.Id, x.Nombre, x.Estado,
                x.Franjas.Count(f => f.Estado == EstadoFranjaEntrega.Activa), x.Version, x.RevisionActual, x.ActualizadaUtc))
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<ZonaCobertura?> ObtenerAsync(Guid zonaId, CancellationToken cancellationToken = default)
    {
        if (zonaId == Guid.Empty) throw new ArgumentException("El identificador de la zona es obligatorio.", nameof(zonaId));
        return db.ZonasCobertura.Include(x => x.Franjas).SingleOrDefaultAsync(x => x.Id == zonaId, cancellationToken);
    }

    public async Task<IReadOnlyList<ZonaCobertura>> ConsultarCoberturaAsync(
        double longitud,
        double latitud,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(longitud) || longitud is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitud));
        if (!double.IsFinite(latitud) || latitud is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitud));

        var point = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326)
            .CreatePoint(new NetTopologySuite.Geometries.Coordinate(longitud, latitud));
        return await db.ZonasCobertura.AsNoTracking()
            .Where(x => x.Estado == EstadoZonaCobertura.Activa && x.Geometria.Covers(point))
            .Include(x => x.Franjas.Where(f => f.Estado == EstadoFranjaEntrega.Activa))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DepartamentoReferenciaResumen>> ListarDepartamentosAsync(CancellationToken cancellationToken = default)
    {
        return await ReferenciasActuales().AsNoTracking().OrderBy(x => x.Codigo)
            .Select(x => new DepartamentoReferenciaResumen(x.Codigo, x.VersionFuente, x.Nombre,
                x.UrlFuente, x.Atribucion, x.Licencia, x.ImportadaUtc))
            .ToListAsync(cancellationToken);
    }

    public Task<DepartamentoReferencia?> ObtenerDepartamentoAsync(string codigo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codigo)) throw new ArgumentException("El código de departamento es obligatorio.", nameof(codigo));
        return db.DepartamentosReferencia.AsNoTracking()
            .Where(x => x.Codigo == codigo.Trim())
            .OrderByDescending(x => x.ImportadaUtc)
            .ThenByDescending(x => x.VersionFuente)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<DepartamentoReferenciaConGeometria?> ObtenerDepartamentoConGeometriaAsync(
        string codigo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codigo)) throw new ArgumentException("El código de departamento es obligatorio.", nameof(codigo));
        var department = await ReferenciasActuales().AsNoTracking()
            .Where(x => x.Codigo == codigo.Trim())
            .Select(x => new { x.Codigo, x.VersionFuente, x.Nombre, x.Geometria })
            .SingleOrDefaultAsync(cancellationToken);
        return department is null ? null : new(department.Codigo, department.VersionFuente, department.Nombre,
            department.Geometria, department.Geometria.EnvelopeInternal);
    }

    public async Task<TerritorioUruguayReferencia?> ObtenerTerritorioUruguayAsync(CancellationToken cancellationToken = default)
    {
        var versions = await ReferenciasActuales().AsNoTracking()
            .OrderBy(x => x.Codigo)
            .Select(x => new { x.Codigo, x.VersionFuente })
            .ToListAsync(cancellationToken);
        if (versions.Count != CatalogoDepartamentosUruguay.DepartmentCodes.Count
            || versions.Select(x => x.VersionFuente).Distinct(StringComparer.Ordinal).Count() != 1)
            return null;

        var version = versions[0].VersionFuente;
        var databaseName = db.Database.GetDbConnection().Database;
        var key = $"{TerritoryCacheKeyPrefix}:{databaseName}:{version}";
        if (cache.TryGetValue<TerritorioUruguayReferencia>(key, out var cached)) return cached;

        await TerritoryCacheGate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue<TerritorioUruguayReferencia>(key, out cached)) return cached;

            var referenceComponents = await db.Database.SqlQueryRaw<Geometry>(
                    "SELECT ST_Collect(\"Geometria\") AS \"Value\" FROM \"DepartamentosReferencia\" WHERE \"VersionFuente\" = @version",
                    new NpgsqlParameter("version", version))
                .SingleOrDefaultAsync(cancellationToken);
            if (referenceComponents is null || referenceComponents.IsEmpty || !referenceComponents.IsValid
                || referenceComponents.SRID != DepartamentoReferencia.GeometriaSrid)
                return null;

            // Keep the exact component collection for ST_Covers; the dissolved outline can omit
            // zero-area coincident source edges, although every source department is covered by the collection.
            var union = await db.Database.SqlQueryRaw<Geometry>(
                    "SELECT ST_UnaryUnion(ST_Collect(\"Geometria\")) AS \"Value\" FROM \"DepartamentosReferencia\" WHERE \"VersionFuente\" = @version",
                    new NpgsqlParameter("version", version))
                .SingleOrDefaultAsync(cancellationToken);
            if (union is null || union.IsEmpty || !union.IsValid
                || union.SRID != DepartamentoReferencia.GeometriaSrid)
                return null;
            var snapshot = new TerritorioUruguayReferencia(CatalogoDepartamentosUruguay.CountryCode,
                version, union, referenceComponents, union.EnvelopeInternal);
            cache.Set(key, snapshot, TimeSpan.FromHours(12));
            return snapshot;
        }
        catch (NetTopologySuite.Geometries.TopologyException)
        {
            return null;
        }
        finally
        {
            TerritoryCacheGate.Release();
        }
    }

    public async Task<ResultadoValidacionTerritorial> ValidarTerritorioUruguayAsync(
        Geometry geometria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometria);
        if (geometria.IsEmpty || !geometria.IsValid || geometria.SRID != DepartamentoReferencia.GeometriaSrid)
            throw new ArgumentException("La geometría debe ser válida, no vacía y tener SRID 4326.", nameof(geometria));

        var territory = await ObtenerTerritorioUruguayAsync(cancellationToken);
        if (territory is null) return ResultadoValidacionTerritorial.CatalogoNoDisponible;
        var parameters = new object[]
        {
            new NpgsqlParameter("territory", territory.GeometriaConDepartamentos),
            new NpgsqlParameter("candidate", geometria)
        };
        var covered = await db.Database.SqlQueryRaw<bool>(
                "SELECT ST_Covers(@territory, @candidate) AS \"Value\"", parameters)
            .SingleAsync(cancellationToken);
        return covered ? ResultadoValidacionTerritorial.Cubierta : ResultadoValidacionTerritorial.FueraDelTerritorio;
    }

    private IQueryable<DepartamentoReferencia> ReferenciasActuales()
        => db.DepartamentosReferencia.Where(department =>
            !db.DepartamentosReferencia.Any(other => other.Codigo == department.Codigo
                && (other.ImportadaUtc > department.ImportadaUtc
                    || (other.ImportadaUtc == department.ImportadaUtc
                        && string.Compare(other.VersionFuente, department.VersionFuente) > 0))));

    public async Task GuardarNuevaAsync(ZonaCobertura zona, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zona);
        if (usuarioId == Guid.Empty) throw new ArgumentException("El usuario autenticado es obligatorio.", nameof(usuarioId));
        if (zona.Version != 0 || zona.RevisionActual != 1)
            throw new InvalidOperationException("La configuración inicial debe guardarse como la revisión 1; usá franjas iniciales al crear la zona.");

        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        db.ZonasCobertura.Add(zona);
        await db.SaveChangesAsync(cancellationToken);
        db.ZonasCoberturaRevisiones.Add(CrearRevision(zona, usuarioId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task GuardarCambioAsync(
        ZonaCobertura zona,
        long versionEsperada,
        Guid usuarioId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zona);
        if (usuarioId == Guid.Empty) throw new ArgumentException("El usuario autenticado es obligatorio.", nameof(usuarioId));
        if (db.Entry(zona).State == EntityState.Detached)
            throw new InvalidOperationException("La zona debe cargarse en el mismo contexto antes de guardarla.");

        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        db.Entry(zona).Property(x => x.Version).OriginalValue = versionEsperada;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            db.ZonasCoberturaRevisiones.Add(CrearRevision(zona, usuarioId));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictoConcurrenciaZonaException(versionEsperada, null);
        }
    }

    private static ZonaCoberturaRevision CrearRevision(ZonaCobertura zona, Guid usuarioId) => new(
        zona.OperadorId,
        zona.Id,
        zona.RevisionActual,
        zona.Version,
        zona.ActualizadaUtc,
        usuarioId,
        ZonaCoberturaSnapshotJson.Crear(zona));
}
