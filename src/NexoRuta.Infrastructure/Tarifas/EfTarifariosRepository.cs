using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Tarifas;

public sealed class EfTarifariosRepository(NexoRutaDbContext db) : ITarifariosRepository
{
    public async Task<TarifarioConsulta> ConsultarAsync(Guid operadorId, CancellationToken cancellationToken = default)
    {
        var root = await db.TarifariosOperador.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperadorId == operadorId, cancellationToken);
        if (root is null) return new(null, []);
        var currentPublicationId = root.VersionVigenteId;
        var versions = await VersionsQuery(operadorId).AsNoTracking()
            .Where(x => x.Estado == EstadoVersionTarifario.Borrador
                || (currentPublicationId.HasValue && x.Id == currentPublicationId.Value))
            .OrderByDescending(x => x.NumeroVersionPublicada)
            .ThenByDescending(x => x.CreadaUtc)
            .ToListAsync(cancellationToken);
        return new(root, versions);
    }

    public async Task<(IReadOnlyList<VersionTarifario> Items, int Total)> ListarPublicacionesAsync(Guid operadorId,
        int pagina, int tamanoPagina, CancellationToken cancellationToken = default)
    {
        var query = db.VersionesTarifario.AsNoTracking().Where(x => x.OperadorId == operadorId
            && x.Estado == EstadoVersionTarifario.Publicada);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.NumeroVersionPublicada).ThenBy(x => x.Id)
            .Skip(checked((pagina - 1) * tamanoPagina)).Take(tamanoPagina)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<VersionTarifario?> ObtenerPublicacionAsync(Guid operadorId, Guid versionId,
        CancellationToken cancellationToken = default)
        => VersionsQuery(operadorId).AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId
            && x.Estado == EstadoVersionTarifario.Publicada, cancellationToken);

    public async Task<VersionTarifario> CrearBorradorAsync(Guid operadorId, Guid usuarioId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        try
        {
            var root = await AsegurarYBloquearTarifarioAsync(operadorId, cancellationToken);
            var existing = await VersionsQuery(operadorId).SingleOrDefaultAsync(
                x => x.Estado == EstadoVersionTarifario.Borrador, cancellationToken);
            if (existing is not null)
                throw new ConflictoTarifarioException(ReglasTarifario.Errors.DraftAlreadyExists,
                    "Ya existe un borrador para este operador.");

            List<TarifaZonaModalidad> sourceRates = [];
            if (root.VersionVigenteId is { } currentId)
            {
                var current = await VersionsQuery(operadorId).SingleOrDefaultAsync(x => x.Id == currentId, cancellationToken)
                    ?? throw new InvalidOperationException("El tarifario referencia una publicación inexistente.");
                if (current.Estado != EstadoVersionTarifario.Publicada)
                    throw new InvalidOperationException("La publicación vigente tiene un estado inválido.");
                sourceRates = current.Tarifas.ToList();
            }

            var draft = VersionTarifario.CrearBorrador(operadorId, usuarioId, sourceRates);
            db.VersionesTarifario.Add(draft);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return draft;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ConflictoVersion();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw ConflictoVersion();
        }
    }

    public async Task<VersionTarifario> EditarBorradorAsync(Guid operadorId, Guid usuarioId,
        long versionEsperada, IReadOnlyCollection<TarifaZonaModalidadConfiguracion> configuracion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        try
        {
            _ = await AsegurarYBloquearTarifarioAsync(operadorId, cancellationToken);
            var draft = await VersionsQuery(operadorId).SingleOrDefaultAsync(
                x => x.Estado == EstadoVersionTarifario.Borrador, cancellationToken)
                ?? throw ConflictoVersion("No existe un borrador editable.");
            draft.VerificarBorrador(versionEsperada);
            await ValidarZonasPropiasAsync(operadorId, configuracion.Select(x => x.ZonaId).Distinct(), cancellationToken);
            draft.ReemplazarConfiguracion(versionEsperada, configuracion);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return draft;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ConflictoVersion();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw ConflictoVersion();
        }
    }

    public async Task<VersionTarifario> PublicarBorradorAsync(Guid operadorId, Guid usuarioId,
        long versionEsperada, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        try
        {
            var root = await AsegurarYBloquearTarifarioAsync(operadorId, cancellationToken);
            var draft = await VersionsQuery(operadorId).SingleOrDefaultAsync(
                x => x.Estado == EstadoVersionTarifario.Borrador, cancellationToken)
                ?? throw ConflictoVersion("No existe un borrador editable.");
            draft.VerificarBorrador(versionEsperada);
            await ValidarZonasPropiasAsync(operadorId, draft.Tarifas.Select(x => x.ZonaId).Distinct(), cancellationToken);
            var configuredZoneIds = draft.Tarifas.Select(rate => rate.ZonaId).Distinct().ToArray();
            var activeZoneIds = await BloquearZonasActivasAsync(operadorId, configuredZoneIds, cancellationToken);
            var publicationNumber = checked(root.UltimoNumeroPublicacion + 1);
            draft.Publicar(versionEsperada, publicationNumber, usuarioId, activeZoneIds);
            root.Publicar(draft);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return draft;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ConflictoVersion();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw ConflictoVersion();
        }
    }

    public async Task<DatosSimulacionTarifario?> ObtenerParaSimularAsync(Guid operadorId, Guid versionId,
        Guid zonaId, ModalidadTarifaria modalidad, CancellationToken cancellationToken = default)
    {
        var version = await VersionsQuery(operadorId).AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == versionId, cancellationToken);
        if (version is null) return null;
        var rate = version.BuscarTarifa(zonaId, modalidad);
        var zoneIsActive = await db.ZonasCobertura.AsNoTracking()
            .AnyAsync(x => x.OperadorId == operadorId && x.Id == zonaId && x.Estado == EstadoZonaCobertura.Activa,
                cancellationToken);
        return new(version, rate, zoneIsActive);
    }

    private IQueryable<VersionTarifario> VersionsQuery(Guid operadorId)
        => db.VersionesTarifario.Where(x => x.OperadorId == operadorId)
            .Include(x => x.Tarifas).ThenInclude(x => x.Ajustes);

    private async Task<TarifarioOperador> AsegurarYBloquearTarifarioAsync(Guid operadorId,
        CancellationToken cancellationToken)
    {
        if (operadorId == Guid.Empty || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("La mutación de tarifario requiere un operador válido y una transacción RLS activa.");

        // All draft writes and publications serialize on the same operator-owned root row.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."TarifariosOperador" ("OperadorId", "VersionVigenteId", "UltimoNumeroPublicacion", "ActualizadoUtc")
            VALUES ({operadorId}, NULL, 0, now())
            ON CONFLICT ("OperadorId") DO NOTHING
            """, cancellationToken);

        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "SELECT \"OperadorId\" FROM public.\"TarifariosOperador\" WHERE \"OperadorId\" = @operatorId FOR UPDATE";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "operatorId";
            parameter.Value = operadorId;
            command.Parameters.Add(parameter);
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("El contexto RLS no permite bloquear el tarifario del operador actual.");
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }

        return await db.TarifariosOperador.SingleAsync(x => x.OperadorId == operadorId, cancellationToken);
    }

    private async Task ValidarZonasPropiasAsync(Guid operadorId, IEnumerable<Guid> zoneIds,
        CancellationToken cancellationToken)
    {
        var expected = zoneIds.ToHashSet();
        if (expected.Count == 0) return;
        var owned = await db.ZonasCobertura.AsNoTracking()
            .Where(x => x.OperadorId == operadorId && expected.Contains(x.Id))
            .Select(x => x.Id).ToHashSetAsync(cancellationToken);
        if (!owned.SetEquals(expected)) throw new TarifaZonaInvalidaException();
    }

    private async Task<HashSet<Guid>> BloquearZonasActivasAsync(Guid operadorId, Guid[] zoneIds,
        CancellationToken cancellationToken)
    {
        if (zoneIds.Length == 0) return [];
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "SELECT \"Id\" FROM public.\"ZonasCobertura\" WHERE \"OperadorId\" = @operatorId AND \"Id\" = ANY(@zoneIds) AND \"Estado\" = 'Activa' FOR SHARE";
            var operatorParameter = command.CreateParameter();
            operatorParameter.ParameterName = "operatorId";
            operatorParameter.Value = operadorId;
            command.Parameters.Add(operatorParameter);
            var zonesParameter = command.CreateParameter();
            zonesParameter.ParameterName = "zoneIds";
            zonesParameter.Value = zoneIds;
            command.Parameters.Add(zonesParameter);
            var result = new HashSet<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetGuid(0));
            return result;
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
    }

    private static ConflictoTarifarioException ConflictoVersion(string message = "El borrador cambió o ya no se puede modificar.")
        => new(ReglasTarifario.Errors.DraftConflict, message);
}
