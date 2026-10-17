using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Features.Organizaciones.Contracts;

public sealed record SolicitudReactivacionResponse(
    Guid Id, string TipoOrganizacion, Guid OrganizacionId, string NombreOrganizacion,
    string Estado, string Motivo, Guid SolicitadaPorUsuarioId, DateTimeOffset SolicitadaEnUtc,
    Guid? ResueltaPorUsuarioId, DateTimeOffset? ResueltaEnUtc, string? MotivoResolucion, long Version)
{
    public static SolicitudReactivacionResponse Desde(SolicitudReactivacionResumen x)
        => new(x.Id, x.TipoOrganizacion.ToString(), x.OrganizacionId, x.NombreOrganizacion,
            x.Estado.ToString(), x.Motivo, x.SolicitadaPorUsuarioId, x.SolicitadaEnUtc,
            x.ResueltaPorUsuarioId, x.ResueltaEnUtc, x.MotivoResolucion, x.Version);
}

public sealed record PaginaSolicitudesReactivacionResponse(
    IReadOnlyList<SolicitudReactivacionResponse> Items, int Pagina, int TamanoPagina, int Total)
{
    public static PaginaSolicitudesReactivacionResponse Desde(PaginaSolicitudesReactivacion x)
        => new(x.Items.Select(SolicitudReactivacionResponse.Desde).ToArray(), x.Pagina, x.TamanoPagina, x.Total);
}

public sealed record SolicitarReactivacionRequest(string Motivo);
public sealed record ResolverSolicitudReactivacionRequest(string Decision, string Motivo, long Version);
