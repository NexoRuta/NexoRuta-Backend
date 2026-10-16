
namespace NexoRuta.Application.Administracion.Dtos;

public sealed record UsuarioOrganizacionResumen(
    Guid UsuarioId,
    Guid AccesoId,
    string Email,
    Guid? RolId,
    string? RolCodigo,
    string? RolNombre,
    bool Activo);
