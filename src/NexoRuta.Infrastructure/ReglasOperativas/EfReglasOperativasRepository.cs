using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.ReglasOperativas;

public sealed class EfReglasOperativasRepository(NexoRutaDbContext db) : IReglasOperativasRepository
{
    public async Task<ReglasOperativasConsulta> ConsultarAsync(Guid operadorId,
        CancellationToken cancellationToken = default)
    {
        var root = await db.ReglasOperativasOperadores.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperadorId == operadorId, cancellationToken);
        if (root is null) return new(null, null, null);
        var versions = await VersionsQuery(operadorId).AsNoTracking()
            .Where(item => item.Estado == EstadoVersionReglasOperativas.Borrador
                || (root.VersionVigenteId.HasValue && item.Id == root.VersionVigenteId.Value))
            .ToListAsync(cancellationToken);
        return new(root,
            versions.SingleOrDefault(item => item.Estado == EstadoVersionReglasOperativas.Borrador),
            root.VersionVigenteId is { } currentId ? versions.SingleOrDefault(item => item.Id == currentId) : null);
    }

    public async Task<(IReadOnlyList<VersionReglasOperativas> Items, int Total)> ListarPublicacionesAsync(
        Guid operadorId, int pagina, int tamanoPagina, CancellationToken cancellationToken = default)
    {
        if (pagina < 1 || tamanoPagina is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pagina));
        var query = db.VersionesReglasOperativas.AsNoTracking().Where(item => item.OperadorId == operadorId
            && item.Estado == EstadoVersionReglasOperativas.Publicada);
        var total = await query.CountAsync(cancellationToken);
        var items = await VersionsQuery(operadorId).AsNoTracking()
            .Where(item => item.Estado == EstadoVersionReglasOperativas.Publicada)
            .OrderByDescending(item => item.NumeroVersionPublicada).ThenBy(item => item.Id)
            .Skip(checked((pagina - 1) * tamanoPagina)).Take(tamanoPagina)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<VersionReglasOperativas?> ObtenerPublicacionAsync(Guid operadorId, Guid versionId,
        CancellationToken cancellationToken = default)
        => VersionsQuery(operadorId).AsNoTracking().SingleOrDefaultAsync(item => item.Id == versionId
            && item.Estado == EstadoVersionReglasOperativas.Publicada, cancellationToken);

    public async Task<VersionReglasOperativas> CrearBorradorAsync(Guid operadorId, Guid usuarioId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        try
        {
            var root = await AsegurarYBloquearAsync(operadorId, cancellationToken);
            if (await VersionsQuery(operadorId).AnyAsync(item => item.Estado == EstadoVersionReglasOperativas.Borrador,
                    cancellationToken))
                throw DraftAlreadyExists();

            VersionReglasOperativas? publication = null;
            if (root.VersionVigenteId is { } currentId)
            {
                publication = await VersionsQuery(operadorId).SingleOrDefaultAsync(item => item.Id == currentId,
                    cancellationToken) ?? throw new InvalidOperationException("La publicación vigente no existe.");
                if (publication.Estado != EstadoVersionReglasOperativas.Publicada)
                    throw new InvalidOperationException("La publicación vigente tiene un estado inválido.");
            }

            var draft = VersionReglasOperativas.CrearBorrador(operadorId, usuarioId, publication);
            root.RegistrarEdicion();
            db.VersionesReglasOperativas.Add(draft);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return draft;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw DraftConflict();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw DraftConflict();
        }
    }

    public async Task<VersionReglasOperativas> EditarBorradorAsync(Guid operadorId, Guid usuarioId,
        long versionEsperada, ReglasOperativasConfiguracion configuracion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        try
        {
            var root = await AsegurarYBloquearAsync(operadorId, cancellationToken);
            var draft = await VersionsQuery(operadorId).SingleOrDefaultAsync(
                item => item.Estado == EstadoVersionReglasOperativas.Borrador, cancellationToken)
                ?? throw DraftConflict("No existe un borrador editable.");
            draft.VerificarBorrador(versionEsperada);
            draft.ReemplazarConfiguracion(versionEsperada, configuracion);
            root.RegistrarEdicion();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return draft;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw DraftConflict();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw DraftConflict();
        }
    }

    public async Task<VersionReglasOperativas> PublicarBorradorAsync(Guid operadorId, Guid usuarioId,
        long versionEsperada, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        try
        {
            var root = await AsegurarYBloquearAsync(operadorId, cancellationToken);
            var draft = await VersionsQuery(operadorId).SingleOrDefaultAsync(
                item => item.Estado == EstadoVersionReglasOperativas.Borrador, cancellationToken)
                ?? throw DraftConflict("No existe un borrador editable.");
            draft.Publicar(versionEsperada, checked(root.UltimoNumeroPublicacion + 1), usuarioId);
            // The version guard checks the old root counter; persist that transition before moving the pointer.
            // Both saves remain inside this transaction, so either the immutable version and pointer commit together or neither does.
            await db.SaveChangesAsync(cancellationToken);
            root.Publicar(draft);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return draft;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw DraftConflict();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw DraftConflict();
        }
    }

    private IQueryable<VersionReglasOperativas> VersionsQuery(Guid operadorId)
        => db.VersionesReglasOperativas.Where(item => item.OperadorId == operadorId)
            .Include(item => item.RequisitosPrueba).Include(item => item.Motivos);

    private async Task<ReglasOperativasOperador> AsegurarYBloquearAsync(Guid operadorId,
        CancellationToken cancellationToken)
    {
        if (operadorId == Guid.Empty || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("La mutación de reglas requiere operador válido y transacción RLS activa.");

        // The operator-owned root also serializes the first draft creation race.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."ReglasOperativasOperador" ("OperadorId", "VersionVigenteId", "UltimoNumeroPublicacion", "ActualizadoUtc")
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
            command.CommandText = "SELECT \"OperadorId\" FROM public.\"ReglasOperativasOperador\" WHERE \"OperadorId\" = @operatorId FOR UPDATE";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "operatorId";
            parameter.Value = operadorId;
            command.Parameters.Add(parameter);
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("El contexto RLS no permite bloquear las reglas del operador actual.");
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
        return await db.ReglasOperativasOperadores.SingleAsync(item => item.OperadorId == operadorId, cancellationToken);
    }

    private static ConflictoReglasOperativasException DraftAlreadyExists()
        => new(NexoRuta.Domain.ReglasOperativas.ReglasOperativas.Errors.DraftAlreadyExists,
            "Ya existe un borrador para este operador.");

    private static ConflictoReglasOperativasException DraftConflict(string message = "El borrador cambió desde que fue consultado.")
        => new(NexoRuta.Domain.ReglasOperativas.ReglasOperativas.Errors.DraftConflict, message);
}
