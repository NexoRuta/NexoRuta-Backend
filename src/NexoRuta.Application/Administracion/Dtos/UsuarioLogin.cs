namespace NexoRuta.Application.Administracion.Dtos;

public sealed record UsuarioLogin(
    Guid UsuarioId,
    Guid AccesoId,
    string Email,
    string PasswordHash);
