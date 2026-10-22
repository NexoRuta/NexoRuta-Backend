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

public sealed record SeguimientoPublico(EstadoEnvio Estado, IdentidadVisualPublica Identidad);
