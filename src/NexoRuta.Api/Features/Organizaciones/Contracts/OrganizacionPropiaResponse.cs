namespace NexoRuta.Api.Features.Organizaciones.Contracts;

public sealed record OrganizacionPropiaResponse(
    Guid Id,
    string Nombre,
    long Version,
    string Estado,
    bool EsPropietario,
    bool TieneDuenoRegistrado);
