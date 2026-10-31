using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NexoRuta.Application.ZonasCobertura.Repositories;
using NexoRuta.Application.ZonasCobertura;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Persistence;
using NetTopologySuite;
using NetTopologySuite.Operation.Union;

namespace NexoRuta.Infrastructure.ZonasCobertura;

public sealed class EfZonasCoberturaRepository : IZonasCoberturaRepository
{
    private static readonly Lazy<DepartamentoCatalogoIncluido> IncludedCatalog =
        new(DepartamentoCatalogImporter.LeerFuenteIncluida, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<TerritorioUruguayReferencia> IncludedTerritory =
        new(CrearTerritorioIncluido, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly NexoRutaDbContext db;

    public EfZonasCoberturaRepository(NexoRutaDbContext db) => this.db = db;

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

    public Task<DepartamentoReferenciaConGeometria?> ObtenerDepartamentoConGeometriaAsync(
        string codigo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codigo)) throw new ArgumentException("El código de departamento es obligatorio.", nameof(codigo));
        cancellationToken.ThrowIfCancellationRequested();
        var department = IncludedCatalog.Value.Departamentos.SingleOrDefault(x =>
            string.Equals(x.Code, codigo.Trim(), StringComparison.Ordinal));
        return Task.FromResult(department is null ? null : new DepartamentoReferenciaConGeometria(
            department.Code, IncludedCatalog.Value.VersionFuente, department.Name,
            department.Geometry, department.Geometry.EnvelopeInternal));
    }

    public Task<TerritorioUruguayReferencia?> ObtenerTerritorioUruguayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<TerritorioUruguayReferencia?>(IncludedTerritory.Value);
    }

    public Task<ResultadoValidacionTerritorial> ValidarTerritorioUruguayAsync(
        Geometry geometria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometria);
        if (geometria.IsEmpty || !geometria.IsValid || geometria.SRID != DepartamentoReferencia.GeometriaSrid)
            throw new ArgumentException("La geometría debe ser válida, no vacía y tener SRID 4326.", nameof(geometria));

        cancellationToken.ThrowIfCancellationRequested();
        if (geometria is Polygon or MultiPolygon
            && IncludedCatalog.Value.Departamentos.Any(departamento =>
                departamento.Geometry.EqualsTopologically(geometria)))
            return Task.FromResult(ResultadoValidacionTerritorial.Cubierta);

        var covered = IncludedTerritory.Value.Geometria.Covers(geometria);
        return Task.FromResult(covered
            ? ResultadoValidacionTerritorial.Cubierta
            : ResultadoValidacionTerritorial.FueraDelTerritorio);
    }

    private static TerritorioUruguayReferencia CrearTerritorioIncluido()
    {
        var catalog = IncludedCatalog.Value;
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: DepartamentoReferencia.GeometriaSrid);
        var departmentGeometries = catalog.Departamentos.Select(x => (Geometry)x.Geometry).ToArray();
        var components = factory.CreateGeometryCollection(departmentGeometries);
        var union = UnaryUnionOp.Union(departmentGeometries);
        var outline = union switch
        {
            Polygon polygon => factory.CreateMultiPolygon([polygon]),
            MultiPolygon multiPolygon => factory.CreateMultiPolygon(multiPolygon.Geometries.Cast<Polygon>().ToArray()),
            _ => throw new InvalidDataException("El GeoJSON oficial no produjo un contorno multipoligonal de Uruguay.")
        };
        if (components.IsEmpty || outline.IsEmpty || !outline.IsValid
            || components.SRID != DepartamentoReferencia.GeometriaSrid
            || outline.SRID != DepartamentoReferencia.GeometriaSrid)
            throw new InvalidDataException("El GeoJSON oficial no produjo geometrías territoriales válidas.");

        return new TerritorioUruguayReferencia(CatalogoDepartamentosUruguay.CountryCode,
            catalog.VersionFuente, outline, components, outline.EnvelopeInternal);
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
