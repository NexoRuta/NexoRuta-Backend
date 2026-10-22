
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Dtos;

public sealed record UsuarioLogin(
    Guid UsuarioId,
    Guid? AccesoId,
    string Email,
    long VersionSesion,
    bool EsPlataforma = false,
    TipoAccesoUsuario? TipoOrganizacion = null,
    EstadoOrganizacion? EstadoOrganizacion = null);
