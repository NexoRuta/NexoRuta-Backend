using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class GestionarBajasOrganizacionUseCase(
    IUsuarioActual usuarioActual,
    IOrganizacionLifecycleRepository organizaciones,
    EsPropietarioOrganizacionUseCase propiedad)
{
    public async Task<SolicitudBajaResumen> SolicitarPropiaAsync(string motivo, CancellationToken ct = default)
    {
        ValidarMotivo(motivo);
        var actor = await usuarioActual.ObtenerAsync(ct);
        var id = ConsultarOrganizacionPropiaUseCase.RequerirOrganizacionId(actor);
        if (!await propiedad.EjecutarAsync(ct))
            throw new AccesoNoPermitidoException();
        return await organizaciones.SolicitarBajaAsync(actor.TipoAcceso, id, actor.UsuarioId,
                   motivo.Trim(), DateTimeOffset.UtcNow, ct)
               ?? throw new OrganizacionActualNoEncontradaException();
    }

    public async Task<IReadOnlyList<SolicitudBajaResumen>> ListarPropiasAsync(CancellationToken ct = default)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        var id = ConsultarOrganizacionPropiaUseCase.RequerirOrganizacionId(actor);
        return await organizaciones.ListarPropiasAsync(actor.TipoAcceso, id, ct);
    }

    public Task<PaginaSolicitudesBaja> ListarAdministrativasAsync(
        EstadoSolicitudBaja? estado, TipoAccesoUsuario? tipo, int pagina, int tamanoPagina, CancellationToken ct = default)
    {
        if (tipo is not null and not (TipoAccesoUsuario.Operador or TipoAccesoUsuario.Comercio))
            throw new ArgumentException("El tipo debe ser Operador o Comercio.");
        if (pagina < 1 || tamanoPagina is < 1 or > 100 || pagina > int.MaxValue / tamanoPagina)
            throw new ArgumentException("La página debe ser positiva y el tamaño debe estar entre 1 y 100.");
        return organizaciones.ListarAsync(new(estado, tipo, pagina, tamanoPagina), ct);
    }

    public async Task<SolicitudBajaResumen> ObtenerAdministrativaAsync(Guid id, CancellationToken ct = default)
        => await organizaciones.ObtenerAsync(id, ct) ?? throw new SolicitudBajaNoEncontradaException();

    public async Task<SolicitudBajaResumen> ResolverAsync(
        Guid id, Guid actorUsuarioId, EstadoSolicitudBaja decision, string motivo, long version, CancellationToken ct = default)
    {
        ValidarMotivo(motivo);
        if (actorUsuarioId == Guid.Empty) throw new AccesoNoPermitidoException();
        if (decision is not (EstadoSolicitudBaja.Aprobada or EstadoSolicitudBaja.Rechazada))
            throw new ArgumentException("La decisión debe ser Aprobada o Rechazada.");
        return await organizaciones.ResolverAsync(id, actorUsuarioId, decision, motivo.Trim(), version,
                   DateTimeOffset.UtcNow, ct)
               ?? throw new SolicitudBajaNoEncontradaException();
    }

    public async Task<EstadoOrganizacionAdministrativa> CambiarEstadoAsync(
        TipoAccesoUsuario tipo, Guid id, Guid actorUsuarioId, EstadoOrganizacion estado,
        string motivo, long version, CancellationToken ct = default)
    {
        ValidarMotivo(motivo);
        if (tipo is not (TipoAccesoUsuario.Operador or TipoAccesoUsuario.Comercio))
            throw new ArgumentException("El tipo debe ser Operador o Comercio.");
        if (estado is not (EstadoOrganizacion.Activa or EstadoOrganizacion.Suspendida))
            throw new ArgumentException("El estado debe ser Activa o Suspendida.");
        if (actorUsuarioId == Guid.Empty) throw new AccesoNoPermitidoException();
        return await organizaciones.CambiarEstadoAsync(tipo, id, actorUsuarioId, estado,
                   motivo.Trim(), version, DateTimeOffset.UtcNow, ct)
               ?? throw new OrganizacionActualNoEncontradaException();
    }

    private static void ValidarMotivo(string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length > 2000)
            throw new ArgumentException("El motivo es obligatorio y no puede superar los 2000 caracteres.", nameof(motivo));
    }
}
