using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Repositories;

public sealed record SolicitudBajaResumen(
    Guid Id,
    TipoAccesoUsuario TipoOrganizacion,
    Guid OrganizacionId,
    string NombreOrganizacion,
    EstadoSolicitudBaja Estado,
    string Motivo,
    Guid SolicitadaPorUsuarioId,
    DateTimeOffset SolicitadaEnUtc,
    Guid? ResueltaPorUsuarioId,
    DateTimeOffset? ResueltaEnUtc,
    string? MotivoResolucion,
    long Version);

public sealed record FiltroSolicitudesBaja(
    EstadoSolicitudBaja? Estado,
    TipoAccesoUsuario? TipoOrganizacion,
    int Pagina,
    int TamanoPagina);

public sealed record PaginaSolicitudesBaja(
    IReadOnlyList<SolicitudBajaResumen> Items,
    int Pagina,
    int TamanoPagina,
    int Total);

public sealed record SolicitudReactivacionResumen(
    Guid Id, TipoAccesoUsuario TipoOrganizacion, Guid OrganizacionId, string NombreOrganizacion,
    EstadoSolicitudReactivacion Estado, string Motivo, Guid SolicitadaPorUsuarioId,
    DateTimeOffset SolicitadaEnUtc, Guid? ResueltaPorUsuarioId, DateTimeOffset? ResueltaEnUtc,
    string? MotivoResolucion, long Version);

public sealed record PaginaSolicitudesReactivacion(
    IReadOnlyList<SolicitudReactivacionResumen> Items, int Pagina, int TamanoPagina, int Total);

public sealed record EstadoOrganizacionAdministrativa(
    Guid Id,
    TipoAccesoUsuario TipoOrganizacion,
    string Nombre,
    EstadoOrganizacion Estado,
    long Version);

public interface IOrganizacionLifecycleRepository
{
    Task<SolicitudReactivacionResumen> SolicitarReactivacionAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, Guid accesoId, Guid solicitanteUsuarioId,
        string motivo, DateTimeOffset solicitadoEnUtc, CancellationToken ct);
    Task<IReadOnlyList<SolicitudReactivacionResumen>> ListarReactivacionesPropiasAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, CancellationToken ct);
    Task<PaginaSolicitudesReactivacion> ListarReactivacionesAsync(
        EstadoSolicitudReactivacion? estado, TipoAccesoUsuario? tipo, int pagina, int tamanoPagina, CancellationToken ct);
    Task<SolicitudReactivacionResumen?> ObtenerReactivacionAsync(Guid id, CancellationToken ct);
    Task<SolicitudReactivacionResumen?> ResolverReactivacionAsync(
        Guid id, Guid actorId, EstadoSolicitudReactivacion decision, string motivo,
        long version, DateTimeOffset fechaUtc, CancellationToken ct);
    Task<SolicitudBajaResumen?> SolicitarBajaAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, Guid solicitanteUsuarioId,
        string motivo, DateTimeOffset solicitadoEnUtc, CancellationToken ct);

    Task<IReadOnlyList<SolicitudBajaResumen>> ListarPropiasAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, CancellationToken ct);

    Task<PaginaSolicitudesBaja> ListarAsync(FiltroSolicitudesBaja filtro, CancellationToken ct);

    Task<SolicitudBajaResumen?> ObtenerAsync(Guid solicitudId, CancellationToken ct);

    Task<SolicitudBajaResumen?> ResolverAsync(
        Guid solicitudId, Guid actorUsuarioId, EstadoSolicitudBaja decision,
        string motivo, long version, DateTimeOffset resueltaEnUtc, CancellationToken ct);

    Task<EstadoOrganizacionAdministrativa?> CambiarEstadoAsync(
        TipoAccesoUsuario tipo, Guid organizacionId, Guid actorUsuarioId,
        EstadoOrganizacion estado, string motivo, long version,
        DateTimeOffset ocurridoEnUtc, CancellationToken ct);
}
