
namespace NexoRuta.Application.Administracion.Dtos;

public sealed record UsuarioOrganizacionResumen(
    Guid UsuarioId,
    Guid AccesoId,
    string Email,
    Guid? RolId,
    string? RolCodigo,
    string? RolNombre,
    bool Activo,
    long Version = 0,
    bool RolProtegido = false,
    bool EsPropietario = false);
