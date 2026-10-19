namespace NexoRuta.Domain.Tarifas;

public sealed class AjusteTarifario
{
    public Guid OperadorId { get; private set; }
    public Guid Id { get; private set; }
    public Guid VersionId { get; private set; }
    public Guid ZonaId { get; private set; }
    public ModalidadTarifaria Modalidad { get; private set; }
    public string Nombre { get; private set; } = "";
    public TipoAjusteTarifario Tipo { get; private set; }
    public decimal? ImporteFijoUyu { get; private set; }
    public decimal? Porcentaje { get; private set; }

    private AjusteTarifario() { }

    public static AjusteTarifario Crear(Guid operadorId, Guid versionId, Guid zonaId,
        ModalidadTarifaria modalidad, string nombre, TipoAjusteTarifario tipo,
        decimal? importeFijoUyu = null, decimal? porcentaje = null, Guid? id = null)
    {
        if (operadorId == Guid.Empty || versionId == Guid.Empty || zonaId == Guid.Empty)
            throw new ArgumentException("Operador, borrador y zona son obligatorios.");
        if (id == Guid.Empty) throw new ArgumentException("El identificador no puede estar vacío.", nameof(id));
        if (!Enum.IsDefined(modalidad)) throw new ArgumentOutOfRangeException(nameof(modalidad));
        if (!Enum.IsDefined(tipo)) throw new ArgumentOutOfRangeException(nameof(tipo));
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 120)
            throw new ArgumentException("El nombre del ajuste es obligatorio y no puede superar 120 caracteres.", nameof(nombre));
        var percent = tipo is TipoAjusteTarifario.RecargoPorcentual or TipoAjusteTarifario.BonificacionPorcentual;
        if (percent ? importeFijoUyu is not null || porcentaje is null : porcentaje is not null || importeFijoUyu is null)
            throw new ArgumentException("El tipo de ajuste requiere exactamente un valor del tipo correspondiente.");
        var value = percent ? porcentaje!.Value : importeFijoUyu!.Value;
        var scale = percent ? ReglasTarifario.PercentageScale : ReglasTarifario.MoneyScale;
        var max = percent ? ReglasTarifario.MaxPercentage : ReglasTarifario.MaxMoney;
        if (value < 0 || value > max || decimal.Round(value, scale) != value)
            throw new ArgumentOutOfRangeException(nameof(value), "El valor del ajuste está fuera del rango o precisión permitidos.");
        return new()
        {
            OperadorId = operadorId,
            Id = id ?? Guid.CreateVersion7(),
            VersionId = versionId,
            ZonaId = zonaId,
            Modalidad = modalidad,
            Nombre = nombre.Trim(),
            Tipo = tipo,
            ImporteFijoUyu = importeFijoUyu,
            Porcentaje = porcentaje
        };
    }

    public AjusteCalculo ACalculo()
        => new(Nombre, Tipo, ImporteFijoUyu ?? Porcentaje ?? throw new InvalidOperationException("El ajuste no tiene valor."));

    internal AjusteTarifario Copiar(Guid operadorId, Guid versionId, Guid zonaId, ModalidadTarifaria modalidad)
        => Crear(operadorId, versionId, zonaId, modalidad, Nombre, Tipo, ImporteFijoUyu, Porcentaje);
}
