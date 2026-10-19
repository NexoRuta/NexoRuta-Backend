using NetTopologySuite.Geometries;

namespace NexoRuta.Domain.ZonasCobertura;

public sealed class DepartamentoReferencia
{
    public const int GeometriaSrid = 4326;

    public string Codigo { get; private set; } = "";
    public string VersionFuente { get; private set; } = "";
    public string Nombre { get; private set; } = "";
    public MultiPolygon Geometria { get; private set; } = null!;
    public string UrlFuente { get; private set; } = "";
    public string Atribucion { get; private set; } = "";
    public string Licencia { get; private set; } = "";
    public DateTimeOffset ImportadaUtc { get; private set; }

    private DepartamentoReferencia() { }

    public DepartamentoReferencia(
        string codigo,
        string versionFuente,
        string nombre,
        Geometry geometria,
        string urlFuente,
        string atribucion,
        string licencia,
        DateTimeOffset importadaUtc)
    {
        Codigo = RequerirTexto(codigo, nameof(codigo), 16);
        VersionFuente = RequerirTexto(versionFuente, nameof(versionFuente), 80);
        Nombre = RequerirTexto(nombre, nameof(nombre), 100);
        Geometria = ValidadorGeometriaCobertura.Normalizar(geometria, nameof(geometria));
        UrlFuente = RequerirTexto(urlFuente, nameof(urlFuente), 500);
        Atribucion = RequerirTexto(atribucion, nameof(atribucion), 300);
        Licencia = string.IsNullOrWhiteSpace(licencia) ? "" : RequerirTexto(licencia, nameof(licencia), 160);
        if (importadaUtc.Offset != TimeSpan.Zero) throw new ArgumentException("La fecha debe estar en UTC.", nameof(importadaUtc));
        ImportadaUtc = importadaUtc;
    }

    private static string RequerirTexto(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
            throw new ArgumentException($"El valor es obligatorio y no puede superar {maxLength} caracteres.", parameterName);
        return value.Trim();
    }
}
