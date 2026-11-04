namespace NexoRuta.Application.Administracion.Dtos;

public sealed record IdentidadVisualOperadorSnapshot(
    Guid OperadorId,
    int Version,
    string? NombreComercial,
    string? CorreoPublico,
    string? TelefonoPublico,
    string? ColorPrimario,
    string? ColorSecundario,
    byte[]? LogoContenido,
    string? LogoTipoContenido);
