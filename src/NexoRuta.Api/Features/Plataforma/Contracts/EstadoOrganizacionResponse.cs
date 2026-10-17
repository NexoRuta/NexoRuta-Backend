using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Features.Plataforma.Contracts;

public sealed record EstadoOrganizacionResponse(Guid Id, string TipoOrganizacion, string Nombre, string Estado, long Version)
{
    public static EstadoOrganizacionResponse Desde(EstadoOrganizacionAdministrativa organization)
        => new(organization.Id, organization.TipoOrganizacion.ToString(), organization.Nombre,
            organization.Estado.ToString(), organization.Version);
}
