namespace NexoRuta.Application.ZonasCobertura;

public sealed record DepartamentoReferenciaResumen(
    string Codigo,
    string VersionFuente,
    string Nombre,
    string UrlFuente,
    string Atribucion,
    string Licencia,
    DateTimeOffset ImportadaUtc);
