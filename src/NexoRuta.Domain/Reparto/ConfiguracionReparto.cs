namespace NexoRuta.Domain.Reparto;

public sealed class ConfiguracionReparto
{
    public Guid OperadorId { get; private set; }
    public long Version { get; private set; }
    public string ZonaHoraria { get; private set; } = "";
    public int MaximoParadas { get; private set; }
    public bool RequiereFotoIntento { get; private set; }
    public bool BloquearDiscrepanciasCarga { get; private set; }

    private ConfiguracionReparto() { }

    public ConfiguracionReparto(Guid operadorId, long version, string zonaHoraria, int maximoParadas,
        bool requiereFotoIntento, bool bloquearDiscrepanciasCarga)
    {
        if (operadorId == Guid.Empty || version < 1) throw new ArgumentException("La configuración requiere operador y versión.");
        if (string.IsNullOrWhiteSpace(zonaHoraria)) throw new ArgumentException("Indicá la zona horaria operativa.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zonaHoraria); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("La zona horaria no es válida."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("La zona horaria no es válida."); }
        if (maximoParadas is < 1 or > 500) throw new ArgumentException("Indicá entre 1 y 500 paradas.");
        OperadorId = operadorId; Version = version; ZonaHoraria = zonaHoraria;
        MaximoParadas = maximoParadas; RequiereFotoIntento = requiereFotoIntento;
        BloquearDiscrepanciasCarga = bloquearDiscrepanciasCarga;
    }
}
