
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Results;

public sealed record SesionIniciada(
    Guid UsuarioId,
    Guid? AccesoId,
    string Email,
    long VersionSesion,
    bool EsPlataforma = false,
    TipoAccesoUsuario? TipoOrganizacion = null,
    bool OrganizacionSuspendida = false,
    long VersionCredencial = 0)
{
    public bool SinOrganizacion => AccesoId is null && !EsPlataforma;
}
