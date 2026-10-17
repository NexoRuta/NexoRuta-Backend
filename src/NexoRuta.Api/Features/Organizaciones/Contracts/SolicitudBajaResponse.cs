using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Features.Organizaciones.Contracts;

public sealed record SolicitudBajaResponse(
    Guid Id,
    string TipoOrganizacion,
    Guid OrganizacionId,
    string NombreOrganizacion,
    string Estado,
    string Motivo,
    Guid SolicitadaPorUsuarioId,
    DateTimeOffset SolicitadaEnUtc,
    Guid? ResueltaPorUsuarioId,
    DateTimeOffset? ResueltaEnUtc,
    string? MotivoResolucion,
    long Version)
{
    public static SolicitudBajaResponse Desde(SolicitudBajaResumen solicitud)
        => new(solicitud.Id, solicitud.TipoOrganizacion.ToString(), solicitud.OrganizacionId,
            solicitud.NombreOrganizacion, solicitud.Estado.ToString(), solicitud.Motivo,
            solicitud.SolicitadaPorUsuarioId, solicitud.SolicitadaEnUtc,
            solicitud.ResueltaPorUsuarioId, solicitud.ResueltaEnUtc, solicitud.MotivoResolucion,
            solicitud.Version);
}
