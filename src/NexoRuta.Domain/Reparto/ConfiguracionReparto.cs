namespace NexoRuta.Domain.Reparto;

public sealed class ConfiguracionReparto
{
    public Guid OperadorId { get; private set; }
    public long Version { get; private set; }
    public string ZonaHoraria { get; private set; } = "";
    public int MaximoParadas { get; private set; }
    public int MaximoIntentos { get; private set; }
    public bool RequiereFirma { get; private set; }
    public bool RequiereFoto { get; private set; }
    public bool RequiereNombre { get; private set; }
    public bool RequiereDocumento { get; private set; }
    public bool RequiereFotoIntento { get; private set; }
    public bool BloquearDiscrepanciasCarga { get; private set; }
    private string[] motivos = [];
    public IReadOnlyList<string> Motivos => Array.AsReadOnly(motivos);

    private ConfiguracionReparto() { }

    public ConfiguracionReparto(Guid operadorId, long version, string zonaHoraria, int maximoParadas, int maximoIntentos,
        bool requiereFirma, bool requiereFoto, bool requiereNombre, bool requiereDocumento,
        bool requiereFotoIntento, bool bloquearDiscrepanciasCarga, IEnumerable<string> motivos)
    {
        if (operadorId == Guid.Empty || version < 1) throw new ArgumentException("La configuración requiere operador y versión.");
        if (string.IsNullOrWhiteSpace(zonaHoraria)) throw new ArgumentException("Indicá la zona horaria operativa.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zonaHoraria); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("La zona horaria no es válida."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("La zona horaria no es válida."); }
        if (maximoParadas is < 1 or > 500 || maximoIntentos is < 1 or > 100)
            throw new ArgumentException("Indicá entre 1 y 500 paradas y entre 1 y 100 intentos.");
        var catalogo = motivos.Select(x => x?.Trim() ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (catalogo.Length is < 1 or > 100 || catalogo.Any(x => x.Length is < 1 or > 120))
            throw new ArgumentException("Configurá entre 1 y 100 motivos no vacíos de hasta 120 caracteres.");
        OperadorId = operadorId; Version = version; ZonaHoraria = zonaHoraria;
        MaximoParadas = maximoParadas; MaximoIntentos = maximoIntentos;
        RequiereFirma = requiereFirma; RequiereFoto = requiereFoto; RequiereNombre = requiereNombre;
        RequiereDocumento = requiereDocumento; RequiereFotoIntento = requiereFotoIntento;
        BloquearDiscrepanciasCarga = bloquearDiscrepanciasCarga;
        this.motivos = catalogo;
    }
}
