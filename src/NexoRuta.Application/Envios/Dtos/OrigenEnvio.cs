namespace NexoRuta.Application.Envios;

public sealed record OrigenEnvio(
    Guid OperadorId,
    Guid ComercioId,
    Guid CreadoPorUsuarioId,
    string UsuarioEmail,
    string OperadorNombre,
    string ComercioNombre);
