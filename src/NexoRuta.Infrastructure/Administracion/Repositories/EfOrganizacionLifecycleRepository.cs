using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfOrganizacionLifecycleRepository(NexoRutaDbContext db) : IOrganizacionLifecycleRepository
{
    private IQueryable<SolicitudReactivacionResumen> ProyectarReactivaciones(IQueryable<SolicitudReactivacionOrganizacion> query)
        => query.Select(x => new SolicitudReactivacionResumen(x.Id, x.TipoOrganizacion, x.OrganizacionId,
            x.OperadorId != null
                ? db.Operadores.Where(o => o.Id == x.OperadorId).Select(o => o.Nombre).FirstOrDefault() ?? ""
                : db.Comercios.Where(c => c.Id == x.ComercioId).Select(c => c.Nombre).FirstOrDefault() ?? "",
            x.Estado, x.Motivo, x.SolicitadaPorUsuarioId, x.SolicitadaEnUtc,
            x.ResueltaPorUsuarioId, x.ResueltaEnUtc, x.MotivoResolucion, x.Version));

    public async Task<SolicitudReactivacionResumen> SolicitarReactivacionAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, Guid accesoId, Guid solicitanteUsuarioId,
        string motivo, DateTimeOffset solicitadoEnUtc, CancellationToken ct)
    {
        await using var transaction = await db.BeginOrJoinAsync(ct);
        await BloquearOrganizacionAsync(tipo, organizacionId, ct);
        var organization = await ObtenerEstadoOrganizacionAsync(tipo, organizacionId, ct);
        if (organization is null) throw new OrganizacionActualNoEncontradaException();
        if (organization.Estado != EstadoOrganizacion.Suspendida)
            throw new ConflictoEstadoOrganizacionException("La organización ya está activa. Ingresá nuevamente.");
        if (!await db.AccesosUsuario.AsNoTracking().AnyAsync(x => x.Id == accesoId &&
                x.UsuarioId == solicitanteUsuarioId && x.EsPropietario && x.Activo &&
                x.TipoOrganizacion == tipo && x.OrganizacionId == organizacionId, ct))
            throw new AccesoNoPermitidoException();
        var request = new SolicitudReactivacionOrganizacion(tipo, organizacionId, solicitanteUsuarioId, solicitadoEnUtc, motivo);
        db.SolicitudesReactivacionOrganizacion.Add(request);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_SolicitudesReactivacion_Pendiente" })
        { throw new ConflictoEstadoOrganizacionException("Ya existe una solicitud de reactivación pendiente."); }
        await transaction.CommitAsync(ct);
        return (await ObtenerReactivacionAsync(request.Id, ct))!;
    }

    public async Task<IReadOnlyList<SolicitudReactivacionResumen>> ListarReactivacionesPropiasAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, CancellationToken ct)
        => await ProyectarReactivaciones(db.SolicitudesReactivacionOrganizacion.AsNoTracking()
            .Where(x => x.TipoOrganizacion == tipo && x.OrganizacionId == organizacionId)
            .OrderByDescending(x => x.SolicitadaEnUtc).ThenByDescending(x => x.Id))
            .ToListAsync(ct);

    public async Task<PaginaSolicitudesReactivacion> ListarReactivacionesAsync(
        EstadoSolicitudReactivacion? estado, TipoAccesoUsuario? tipo, int pagina, int tamanoPagina, CancellationToken ct)
    {
        var query = db.SolicitudesReactivacionOrganizacion.AsNoTracking();
        if (estado is { } state) query = query.Where(x => x.Estado == state);
        if (tipo is { } kind) query = query.Where(x => x.TipoOrganizacion == kind);
        var total = await query.CountAsync(ct);
        var items = await ProyectarReactivaciones(query.OrderByDescending(x => x.SolicitadaEnUtc).ThenByDescending(x => x.Id)
            .Skip((pagina - 1) * tamanoPagina).Take(tamanoPagina)).ToListAsync(ct);
        return new(items, pagina, tamanoPagina, total);
    }

    public Task<SolicitudReactivacionResumen?> ObtenerReactivacionAsync(Guid id, CancellationToken ct)
        => ProyectarReactivaciones(db.SolicitudesReactivacionOrganizacion.AsNoTracking().Where(x => x.Id == id))
            .SingleOrDefaultAsync(ct);

    public async Task<SolicitudReactivacionResumen?> ResolverReactivacionAsync(
        Guid id, Guid actorId, EstadoSolicitudReactivacion decision, string motivo,
        long version, DateTimeOffset fechaUtc, CancellationToken ct)
    {
        await using var transaction = await db.BeginOrJoinAsync(ct);
        try
        {
            var request = await db.SolicitudesReactivacionOrganizacion.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (request is null) return null;
            await BloquearOrganizacionAsync(request.TipoOrganizacion, request.OrganizacionId, ct);
            await db.Entry(request).ReloadAsync(ct);
            if (request.Estado == EstadoSolicitudReactivacion.Pendiente && request.Version != version)
                throw new ConflictoEstadoOrganizacionException("La solicitud cambió. Volvé a cargarla.");
            bool changed;
            try { changed = request.Resolver(decision, actorId, fechaUtc, motivo); }
            catch (InvalidOperationException error) { throw new ConflictoEstadoOrganizacionException(error.Message); }
            if (!changed) return await ObtenerReactivacionAsync(id, ct);
            var state = await ObtenerEstadoOrganizacionAsync(request.TipoOrganizacion, request.OrganizacionId, ct);
            if (state?.Estado != EstadoOrganizacion.Suspendida)
                throw new ConflictoEstadoOrganizacionException("La organización ya está activa.");
            if (decision == EstadoSolicitudReactivacion.Aprobada)
            {
                var transition = await CambiarEstadoTrackedAsync(request.TipoOrganizacion, request.OrganizacionId,
                    EstadoOrganizacion.Activa, state.Version, ct);
                if (transition is { } result && result.Previous != result.Current)
                    db.CambiosEstadoOrganizacion.Add(new CambioEstadoOrganizacion(request.TipoOrganizacion,
                        request.OrganizacionId, result.Previous, result.Current, actorId, fechaUtc, motivo));
            }
            db.Entry(request).Property(x => x.Version).OriginalValue = version;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await ObtenerReactivacionAsync(id, ct);
        }
        catch (DbUpdateConcurrencyException)
        { throw new ConflictoEstadoOrganizacionException("La solicitud cambió concurrentemente. Volvé a cargarla."); }
    }

    private Task BloquearOrganizacionAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => tipo == TipoAccesoUsuario.Comercio
            ? db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Comercios\" SET \"Version\" = \"Version\" WHERE \"Id\" = {id}", ct)
            : db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Operadores\" SET \"Version\" = \"Version\" WHERE \"Id\" = {id}", ct);

    private IQueryable<SolicitudBajaResumen> Proyectar(IQueryable<SolicitudBajaOrganizacion> query)
        => query.Select(x => new SolicitudBajaResumen(
            x.Id,
            x.TipoOrganizacion,
            x.OrganizacionId,
            x.OperadorId != null
                ? db.Operadores.Where(o => o.Id == x.OperadorId).Select(o => o.Nombre).FirstOrDefault() ?? ""
                : db.Comercios.Where(c => c.Id == x.ComercioId).Select(c => c.Nombre).FirstOrDefault() ?? "",
            x.Estado,
            x.Motivo,
            x.SolicitadaPorUsuarioId,
            x.SolicitadaEnUtc,
            x.ResueltaPorUsuarioId,
            x.ResueltaEnUtc,
            x.MotivoResolucion,
            x.Version));

    public async Task<SolicitudBajaResumen?> SolicitarBajaAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, Guid solicitanteUsuarioId,
        string motivo, DateTimeOffset solicitadoEnUtc, CancellationToken ct)
    {
        var nombre = await NombreOrganizacionAsync(tipo, organizacionId, ct);
        if (nombre is null) return null;
        var solicitud = new SolicitudBajaOrganizacion(tipo, organizacionId, solicitanteUsuarioId, solicitadoEnUtc, motivo);
        db.SolicitudesBajaOrganizacion.Add(solicitud);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException error) when (EsConflictoSolicitudPendiente(error))
        {
            throw new SolicitudBajaPendienteException();
        }
        return new(solicitud.Id, tipo, organizacionId, nombre, solicitud.Estado, solicitud.Motivo,
            solicitanteUsuarioId, solicitadoEnUtc, null, null, null, solicitud.Version);
    }

    public async Task<IReadOnlyList<SolicitudBajaResumen>> ListarPropiasAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, CancellationToken ct)
        => await Proyectar(db.SolicitudesBajaOrganizacion.AsNoTracking()
                .Where(x => x.TipoOrganizacion == tipo && x.OrganizacionId == organizacionId)
                .OrderByDescending(x => x.SolicitadaEnUtc).ThenByDescending(x => x.Id))
            .ToListAsync(ct);

    public async Task<PaginaSolicitudesBaja> ListarAsync(FiltroSolicitudesBaja filtro, CancellationToken ct)
    {
        var query = db.SolicitudesBajaOrganizacion.AsNoTracking();
        if (filtro.Estado is { } estado) query = query.Where(x => x.Estado == estado);
        if (filtro.TipoOrganizacion is { } tipo) query = query.Where(x => x.TipoOrganizacion == tipo);
        var total = await query.CountAsync(ct);
        var items = await Proyectar(query.OrderByDescending(x => x.SolicitadaEnUtc).ThenByDescending(x => x.Id)
            .Skip((filtro.Pagina - 1) * filtro.TamanoPagina).Take(filtro.TamanoPagina)).ToListAsync(ct);
        return new(items, filtro.Pagina, filtro.TamanoPagina, total);
    }

    public Task<SolicitudBajaResumen?> ObtenerAsync(Guid solicitudId, CancellationToken ct)
        => Proyectar(db.SolicitudesBajaOrganizacion.AsNoTracking().Where(x => x.Id == solicitudId))
            .SingleOrDefaultAsync(ct);

    public async Task<SolicitudBajaResumen?> ResolverAsync(
        Guid solicitudId, Guid actorUsuarioId, EstadoSolicitudBaja decision,
        string motivo, long version, DateTimeOffset resueltaEnUtc, CancellationToken ct)
    {
        await using var transaction = await db.BeginOrJoinAsync(ct);
        try
        {
            var solicitud = await db.SolicitudesBajaOrganizacion.SingleOrDefaultAsync(x => x.Id == solicitudId, ct);
            if (solicitud is null) return null;
            if (solicitud.Estado == EstadoSolicitudBaja.Pendiente && solicitud.Version != version)
                throw new ConflictoEstadoOrganizacionException("La solicitud cambió desde la última consulta.");
            try
            {
                var changed = solicitud.Resolver(decision, actorUsuarioId, resueltaEnUtc, motivo);
                if (!changed)
                {
                    await transaction.CommitAsync(ct);
                    return await ObtenerAsync(solicitudId, ct);
                }
            }
            catch (InvalidOperationException error)
            {
                throw new ConflictoEstadoOrganizacionException(error.Message);
            }
            var tipo = solicitud.TipoOrganizacion;
            var organizacionId = solicitud.OrganizacionId;
            var anterior = await ObtenerEstadoOrganizacionAsync(tipo, organizacionId, ct);
            if (anterior is null) return null;

            if (decision == EstadoSolicitudBaja.Aprobada)
            {
                var changedState = await CambiarEstadoTrackedAsync(tipo, organizacionId, EstadoOrganizacion.Suspendida, version: null, ct);
                if (changedState is { } transition)
                {
                    db.CambiosEstadoOrganizacion.Add(new CambioEstadoOrganizacion(
                        tipo, organizacionId, transition.Previous, transition.Current, actorUsuarioId,
                        resueltaEnUtc, motivo, solicitudId));
                    await RevocarSesionesOrganizacionAsync(tipo, organizacionId, ct);
                }
            }

            db.Entry(solicitud).Property(x => x.Version).OriginalValue = version;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await ObtenerAsync(solicitudId, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictoEstadoOrganizacionException("La solicitud u organización cambió concurrentemente. Volvé a cargar los datos.");
        }
    }

    public async Task<EstadoOrganizacionAdministrativa?> CambiarEstadoAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, Guid actorUsuarioId,
        EstadoOrganizacion estado, string motivo, long version,
        DateTimeOffset ocurridoEnUtc, CancellationToken ct)
    {
        await using var transaction = await db.BeginOrJoinAsync(ct);
        try
        {
            await BloquearOrganizacionAsync(tipo, organizacionId, ct);
            var transition = await CambiarEstadoTrackedAsync(tipo, organizacionId, estado, version, ct);
            if (transition is null)
            {
                await transaction.CommitAsync(ct);
                return await ObtenerEstadoOrganizacionAsync(tipo, organizacionId, ct);
            }
            if (transition.Value.Previous == transition.Value.Current)
            {
                await transaction.CommitAsync(ct);
                return await ObtenerEstadoOrganizacionAsync(tipo, organizacionId, ct);
            }

            db.CambiosEstadoOrganizacion.Add(new CambioEstadoOrganizacion(
                tipo, organizacionId, transition.Value.Previous, transition.Value.Current,
                actorUsuarioId, ocurridoEnUtc, motivo));
            if (estado == EstadoOrganizacion.Suspendida)
                await RevocarSesionesOrganizacionAsync(tipo, organizacionId, ct);
            else
            {
                var pending = await db.SolicitudesReactivacionOrganizacion.SingleOrDefaultAsync(x =>
                    x.TipoOrganizacion == tipo && x.OrganizacionId == organizacionId &&
                    x.Estado == EstadoSolicitudReactivacion.Pendiente, ct);
                pending?.Resolver(EstadoSolicitudReactivacion.AtendidaPorSoporte, actorUsuarioId, ocurridoEnUtc, motivo);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await ObtenerEstadoOrganizacionAsync(tipo, organizacionId, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictoEstadoOrganizacionException("La organización cambió desde la última consulta. Volvé a cargar los datos.");
        }
    }

    private async Task<(EstadoOrganizacion Previous, EstadoOrganizacion Current)?> CambiarEstadoTrackedAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, EstadoOrganizacion estado, long? version, CancellationToken ct)
    {
        try
        {
            var row = await db.Database.SqlQueryRaw<OrganizationStateChangeRow>(
                "SELECT * FROM " + RlsSqlFunctionContract.SetOrganizationState + "({0}, {1}, {2}, {3})",
                tipo.ToString(), organizacionId, estado.ToString(), version!)
                .SingleOrDefaultAsync(ct);
            if (row is null || !Enum.TryParse<EstadoOrganizacion>(row.EstadoAnterior, out var previous) ||
                !Enum.TryParse<EstadoOrganizacion>(row.EstadoNuevo, out var current))
                return null;
            return (previous, current);
        }
        catch (PostgresException exception) when (exception.SqlState == "P0001")
        {
            throw new DbUpdateConcurrencyException("The organization version is stale.", exception);
        }
    }

    private Task<EstadoOrganizacionAdministrativa?> ObtenerEstadoOrganizacionAsync(
        TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => tipo == TipoAccesoUsuario.Operador
            ? db.Operadores.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new EstadoOrganizacionAdministrativa(x.Id, tipo, x.Nombre, x.Estado, x.Version)).SingleOrDefaultAsync(ct)
            : db.Comercios.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new EstadoOrganizacionAdministrativa(x.Id, tipo, x.Nombre, x.Estado, x.Version)).SingleOrDefaultAsync(ct);

    private Task<string?> NombreOrganizacionAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => tipo == TipoAccesoUsuario.Operador
            ? db.Operadores.AsNoTracking().Where(x => x.Id == id).Select(x => x.Nombre).SingleOrDefaultAsync(ct)
            : tipo == TipoAccesoUsuario.Comercio
                ? db.Comercios.AsNoTracking().Where(x => x.Id == id).Select(x => x.Nombre).SingleOrDefaultAsync(ct)
                : throw new ArgumentOutOfRangeException(nameof(tipo));

    private async Task<int> RevocarSesionesOrganizacionAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => await db.Database.SqlQueryRaw<int>(
            "SELECT " + RlsSqlFunctionContract.RevokeOrgSessions + "({0}, {1}) AS \"Value\"", tipo.ToString(), id)
            .SingleAsync(ct);

    private sealed record OrganizationStateChangeRow(string EstadoAnterior, string EstadoNuevo);

    private static bool EsConflictoSolicitudPendiente(DbUpdateException error)
        => error.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_SolicitudesBaja_Pendiente"
        };
}
