using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Context;

public sealed record ContextoOrganizacionSuspendida(
    Guid UsuarioId,
    Guid AccesoId,
    Guid OrganizacionId,
    TipoAccesoUsuario TipoOrganizacion,
    string NombreOrganizacion,
    long VersionSesion);
