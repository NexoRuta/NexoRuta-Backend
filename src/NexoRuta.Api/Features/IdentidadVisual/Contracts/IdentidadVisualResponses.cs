namespace NexoRuta.Api.Features.IdentidadVisual.Contracts;

public sealed record IdentidadVisualPublicaResponse(
    string Nombre,
    string? CorreoPublico,
    string? TelefonoPublico,
    string? ColorPrimario,
    string? ColorSecundario,
    byte[]? LogoContenido,
    string? LogoTipoContenido);

public sealed record IdentidadVisualAdministracionResponse(
    int Version,
    string? NombreComercial,
    string NombreMostrado,
    string? CorreoPublico,
    string? TelefonoPublico,
    string? ColorPrimario,
    string? ColorSecundario,
    byte[]? LogoContenido,
    string? LogoTipoContenido);

public sealed record IdentidadVisualVersionResponse(
    int Version,
    Guid AutorUsuarioId,
    DateTimeOffset CreadoEnUtc,
    string? NombreComercial,
    string? CorreoPublico,
    string? TelefonoPublico,
    string? ColorPrimario,
    string? ColorSecundario,
    byte[]? LogoContenido,
    string? LogoTipoContenido);

public sealed record IdentidadVisualHistorialResponse(
    IReadOnlyList<IdentidadVisualVersionResponse> Versiones,
    int? CursorAnterior);
