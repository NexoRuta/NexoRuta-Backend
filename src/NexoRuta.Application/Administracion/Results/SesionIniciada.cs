
namespace NexoRuta.Application.Administracion.Results;

public sealed record SesionIniciada(
    Guid UsuarioId,
    Guid AccesoId,
    string Email,
    long VersionSesion);
