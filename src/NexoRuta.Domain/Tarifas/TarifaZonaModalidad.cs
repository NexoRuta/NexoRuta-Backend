namespace NexoRuta.Domain.Tarifas;

public sealed class TarifaZonaModalidad
{
    private readonly List<AjusteTarifario> ajustes = [];

    public Guid OperadorId { get; private set; }
    public Guid VersionId { get; private set; }
    public Guid ZonaId { get; private set; }
    public ModalidadTarifaria Modalidad { get; private set; }
    public decimal ImporteBaseUyu { get; private set; }
    public decimal PrecioPorKilogramoUyu { get; private set; }
    public decimal PrecioPorMetroCubicoUyu { get; private set; }
    public IReadOnlyCollection<AjusteTarifario> Ajustes => ajustes;

    private TarifaZonaModalidad() { }

    public static TarifaZonaModalidad Crear(Guid operadorId, Guid versionId, Guid zonaId,
        ModalidadTarifaria modalidad, decimal importeBaseUyu, decimal precioPorKilogramoUyu,
        decimal precioPorMetroCubicoUyu, IEnumerable<AjusteTarifario>? ajustes = null)
    {
        if (operadorId == Guid.Empty || versionId == Guid.Empty || zonaId == Guid.Empty)
            throw new ArgumentException("Operador, borrador y zona son obligatorios.");
        if (!Enum.IsDefined(modalidad)) throw new ArgumentOutOfRangeException(nameof(modalidad));
        ValidarValor(importeBaseUyu, ReglasTarifario.MoneyScale, ReglasTarifario.MaxMoney, nameof(importeBaseUyu));
        ValidarValor(precioPorKilogramoUyu, ReglasTarifario.RateScale, ReglasTarifario.MaxRate, nameof(precioPorKilogramoUyu));
        ValidarValor(precioPorMetroCubicoUyu, ReglasTarifario.RateScale, ReglasTarifario.MaxRate, nameof(precioPorMetroCubicoUyu));
        var rate = new TarifaZonaModalidad
        {
            OperadorId = operadorId,
            VersionId = versionId,
            ZonaId = zonaId,
            Modalidad = modalidad,
            ImporteBaseUyu = importeBaseUyu,
            PrecioPorKilogramoUyu = precioPorKilogramoUyu,
            PrecioPorMetroCubicoUyu = precioPorMetroCubicoUyu
        };
        if (ajustes is not null)
        {
            var copy = ajustes.ToList();
            if (copy.Any(x => x is null)) throw new ArgumentException("La lista contiene un ajuste nulo.", nameof(ajustes));
            if (copy.Select(x => x.Id).Distinct().Count() != copy.Count)
                throw new ArgumentException("Los identificadores de ajustes deben ser únicos.", nameof(ajustes));
            rate.ajustes.AddRange(copy.Select(x => x.Copiar(operadorId, versionId, zonaId, modalidad)));
        }
        return rate;
    }

    public TarifaCalculo ACalculo(VersionTarifario version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.OperadorId != OperadorId || version.Id != VersionId)
            throw new ArgumentException("La tarifa pertenece a otra versión.", nameof(version));
        return new(ZonaId, Modalidad, ImporteBaseUyu, PrecioPorKilogramoUyu,
            PrecioPorMetroCubicoUyu, ajustes.Select(x => x.ACalculo()).ToArray(),
            version.Id, version.NumeroVersionPublicada, version.VersionConcurrencia, version.Moneda);
    }

    internal TarifaZonaModalidad Copiar(Guid nuevaVersionId)
        => Crear(OperadorId, nuevaVersionId, ZonaId, Modalidad, ImporteBaseUyu,
            PrecioPorKilogramoUyu, PrecioPorMetroCubicoUyu, ajustes);

    private static void ValidarValor(decimal value, int scale, decimal max, string name)
    {
        if (value < 0 || value > max || decimal.Round(value, scale) != value)
            throw new ArgumentOutOfRangeException(name, "El valor está fuera del rango o precisión permitidos.");
    }
}

public sealed record AjusteTarifarioConfiguracion(
    string Nombre,
    TipoAjusteTarifario Tipo,
    decimal? ImporteFijoUyu = null,
    decimal? Porcentaje = null);

public sealed record TarifaZonaModalidadConfiguracion(
    Guid ZonaId,
    ModalidadTarifaria Modalidad,
    decimal ImporteBaseUyu,
    decimal PrecioPorKilogramoUyu,
    decimal PrecioPorMetroCubicoUyu,
    IReadOnlyList<AjusteTarifarioConfiguracion> Ajustes);
