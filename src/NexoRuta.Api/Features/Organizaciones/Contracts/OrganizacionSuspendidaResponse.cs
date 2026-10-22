namespace NexoRuta.Api.Features.Organizaciones.Contracts;

public sealed record OrganizacionSuspendidaResponse(
    Guid OrganizacionId,
    string TipoOrganizacion,
    string Nombre,
    string Estado,
    string Mensaje,
    bool EsPropietario);
