using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class GestionarReactivacionesOrganizacionUseCase(IOrganizacionLifecycleRepository repository)
{
    public Task<SolicitudReactivacionResumen> SolicitarAsync(
        ContextoOrganizacionSuspendida actor, string motivo, CancellationToken ct)
    {
        ValidarMotivo(motivo);
        return repository.SolicitarReactivacionAsync(actor.TipoOrganizacion, actor.OrganizacionId,
            actor.AccesoId, actor.UsuarioId, motivo.Trim(), DateTimeOffset.UtcNow, ct);
    }

    public Task<IReadOnlyList<SolicitudReactivacionResumen>> ListarPropiasAsync(
        ContextoOrganizacionSuspendida actor, CancellationToken ct)
        => repository.ListarReactivacionesPropiasAsync(actor.TipoOrganizacion, actor.OrganizacionId, ct);

    public Task<PaginaSolicitudesReactivacion> ListarAsync(EstadoSolicitudReactivacion? estado,
        TipoAccesoUsuario? tipo, int pagina, int tamanoPagina, CancellationToken ct)
    {
        if (tipo is not null and not (TipoAccesoUsuario.Comercio or TipoAccesoUsuario.Operador) ||
            pagina < 1 || tamanoPagina is < 1 or > 100 || pagina > int.MaxValue / tamanoPagina)
            throw new ArgumentException("Los filtros o la paginación no son válidos.");
        return repository.ListarReactivacionesAsync(estado, tipo, pagina, tamanoPagina, ct);
    }

    public async Task<SolicitudReactivacionResumen> ObtenerAsync(Guid id, CancellationToken ct)
        => await repository.ObtenerReactivacionAsync(id, ct)
            ?? throw new SolicitudReactivacionNoEncontradaException();

    public async Task<SolicitudReactivacionResumen> ResolverAsync(Guid id, Guid actorId,
        EstadoSolicitudReactivacion decision, string motivo, long version, CancellationToken ct)
    {
        ValidarMotivo(motivo);
        if (actorId == Guid.Empty || decision is not (EstadoSolicitudReactivacion.Aprobada or EstadoSolicitudReactivacion.Rechazada))
            throw new ArgumentException("La decisión o el actor no son válidos.");
        return await repository.ResolverReactivacionAsync(id, actorId, decision, motivo.Trim(), version,
            DateTimeOffset.UtcNow, ct) ?? throw new SolicitudReactivacionNoEncontradaException();
    }

    private static void ValidarMotivo(string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length > 2000)
            throw new ArgumentException("El motivo es obligatorio y no puede superar los 2000 caracteres.");
    }
}

public sealed class SolicitudReactivacionNoEncontradaException()
    : Exception("No se encontró la solicitud de reactivación.");
