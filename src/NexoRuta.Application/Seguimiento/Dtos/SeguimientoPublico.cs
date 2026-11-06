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
{ public VentanaEstimadaPublica? VentanaEstimada { get; init; } public IReadOnlyList<EventoPublico> Historial { get; init; } = []; public IReadOnlyList<BultoPublico> Bultos { get; init; } = []; public int TotalBultos { get; init; } public int BultosEntregados { get; init; } public int BultosDevueltos { get; init; } public bool EsRetiro { get; init; } }

public sealed record EventoPublico(string Estado, DateTimeOffset? OcurridaEnUtc, DateTimeOffset RegistradaEnUtc);
public sealed record BultoPublico(int Numero, string Resultado);

public sealed record VentanaEstimadaPublica(DateTimeOffset DesdeUtc, DateTimeOffset HastaUtc, string Origen, DateTimeOffset ActualizadaEnUtc);
