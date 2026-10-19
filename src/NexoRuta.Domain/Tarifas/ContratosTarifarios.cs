namespace NexoRuta.Domain.Tarifas;

public enum ModalidadTarifaria { Estandar, Urgente }
public enum TipoAjusteTarifario { RecargoFijo, RecargoPorcentual, BonificacionFija, BonificacionPorcentual }
public enum MonedaTarifaria { UYU }
public enum EstadoVersionTarifario { Borrador, Publicada }
public enum BaseAjusteTarifario { ImporteFijo, SubtotalOriginal }

public static class ReglasTarifario
{
    public const int NombreMaxLength = 120;
    public const int PublicationNumberInitial = 1;
    public const int MoneyPrecision = 14;
    public const int MoneyScale = 2;
    public const int RatePrecision = 14;
    public const int RateScale = 6;
    public const int PercentagePrecision = 5;
    public const int PercentageScale = 2;
    public const decimal MaxMoney = 999_999_999_999.99m;
    public const decimal MaxRate = 99_999_999.999999m;
    public const decimal MaxBultoMeasure = 9_999_999_999.99m;
    public const decimal MaxPercentage = 100m;
    public const decimal CubicCentimetersPerCubicMeter = 1_000_000m;
    public const decimal GramsPerKilogram = 1_000m;
    public const int FinalMoneyScale = 2;
    public const MidpointRounding FinalRounding = MidpointRounding.AwayFromZero;

    public static class Errors
    {
        public const string DraftConflict = "tariff.draft_conflict";
        public const string DraftAlreadyExists = "tariff.draft_already_exists";
        public const string NoActiveOffer = "tariff.no_active_offer";
        public const string RateNotConfigured = "tariff.rate_not_configured";
        public const string ZoneInactive = "tariff.zone_inactive";
    }
}

public sealed class ConflictoTarifarioException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class TarifaNoConfiguradaException(Guid zonaId, ModalidadTarifaria modalidad)
    : InvalidOperationException("No existe una tarifa para la zona y modalidad solicitadas.")
{
    public string Code => ReglasTarifario.Errors.RateNotConfigured;
    public Guid ZonaId { get; } = zonaId;
    public ModalidadTarifaria Modalidad { get; } = modalidad;
}
