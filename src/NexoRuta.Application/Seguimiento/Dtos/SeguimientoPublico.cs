using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Seguimiento.Dtos;

public sealed record IdentidadVisualPublica(
    string Nombre,
    string? CorreoPublico,
    string? TelefonoPublico,
    string? ColorPrimario,
    string? ColorSecundario,
    byte[]? LogoContenido,
    string? LogoTipoContenido);

public sealed record SeguimientoPublico(EstadoEnvio Estado, IdentidadVisualPublica Identidad)
{ public int TotalBultos { get; init; } public int BultosEntregados { get; init; } public int BultosDevueltos { get; init; } public bool EsRetiro { get; init; } }
