namespace NexoRuta.Application.Reparto.Dtos;

public sealed record RepartidorResumen(Guid AccesoId, Guid UsuarioId, Guid OperadorId,
    string Email, string Nombre, bool Activo, long Version);
