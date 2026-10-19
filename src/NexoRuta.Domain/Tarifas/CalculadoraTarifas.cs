namespace NexoRuta.Domain.Tarifas;

public sealed record BultoCalculo(decimal PesoGramos, decimal LargoCentimetros, decimal AnchoCentimetros, decimal AltoCentimetros);

public sealed record AjusteCalculo(string Nombre, TipoAjusteTarifario Tipo, decimal Valor)
{
    public AjusteCalculo Validar()
    {
        if (string.IsNullOrWhiteSpace(Nombre) || Nombre.Trim().Length > 120)
            throw new ArgumentException("El nombre del ajuste es obligatorio y no puede superar 120 caracteres.", nameof(Nombre));
        if (!Enum.IsDefined(Tipo)) throw new ArgumentOutOfRangeException(nameof(Tipo));
        var percentage = Tipo is TipoAjusteTarifario.RecargoPorcentual or TipoAjusteTarifario.BonificacionPorcentual;
        if (percentage ? Valor is < 0 or > ReglasTarifario.MaxPercentage : Valor is < 0 or > ReglasTarifario.MaxMoney)
            throw new ArgumentOutOfRangeException(nameof(Valor), "El valor del ajuste está fuera del rango permitido.");
        var scale = percentage ? ReglasTarifario.PercentageScale : ReglasTarifario.MoneyScale;
        if (decimal.Round(Valor, scale) != Valor)
            throw new ArgumentOutOfRangeException(nameof(Valor), "El valor del ajuste supera la precisión permitida.");
        return this with { Nombre = Nombre.Trim() };
    }
}

public sealed record TarifaCalculo(
    Guid ZonaId,
    ModalidadTarifaria Modalidad,
    decimal ImporteBaseUyu,
    decimal PrecioPorKilogramoUyu,
    decimal PrecioPorMetroCubicoUyu,
    IReadOnlyList<AjusteCalculo> Ajustes,
    Guid? VersionTarifarioId = null,
    int? NumeroVersionPublicada = null,
    long? VersionConcurrencia = null,
    MonedaTarifaria Moneda = MonedaTarifaria.UYU);

public sealed record AjusteTarifarioAplicado(
    string Nombre,
    TipoAjusteTarifario Tipo,
    BaseAjusteTarifario Base,
    decimal ValorConfigurado,
    decimal ImporteUyu);

public sealed record ResultadoCalculoTarifa(
    Guid ZonaId,
    ModalidadTarifaria Modalidad,
    Guid? VersionTarifarioId,
    int? NumeroVersionPublicada,
    long? VersionConcurrencia,
    MonedaTarifaria Moneda,
    decimal PesoTotalKilogramos,
    decimal VolumenTotalMetrosCubicos,
    decimal ImporteBaseUyu,
    decimal ComponentePesoUyu,
    decimal ComponenteVolumenUyu,
    decimal SubtotalUyu,
    IReadOnlyList<AjusteTarifarioAplicado> Ajustes,
    decimal TotalRecargosUyu,
    decimal TotalBonificacionesUyu,
    decimal TotalExactoUyu,
    decimal TotalFinalUyu,
    bool PisoCeroAplicado);

public static class CalculadoraTarifas
{
    public static ResultadoCalculoTarifa Calcular(TarifaCalculo tarifa, IReadOnlyCollection<BultoCalculo> bultos)
    {
        ArgumentNullException.ThrowIfNull(tarifa);
        ArgumentNullException.ThrowIfNull(bultos);
        if (tarifa.ZonaId == Guid.Empty) throw new ArgumentException("La zona es obligatoria.", nameof(tarifa));
        if (!Enum.IsDefined(tarifa.Modalidad)) throw new ArgumentOutOfRangeException(nameof(tarifa.Modalidad));
        if (!Enum.IsDefined(tarifa.Moneda)) throw new ArgumentOutOfRangeException(nameof(tarifa.Moneda));
        if (tarifa.VersionTarifarioId == Guid.Empty) throw new ArgumentException("La versión tarifaria no puede ser vacía.", nameof(tarifa));
        if (tarifa.NumeroVersionPublicada is <= 0) throw new ArgumentOutOfRangeException(nameof(tarifa.NumeroVersionPublicada));
        if (tarifa.VersionConcurrencia is < 0) throw new ArgumentOutOfRangeException(nameof(tarifa.VersionConcurrencia));
        if (bultos.Count == 0) throw new ArgumentException("Se requiere al menos un bulto.", nameof(bultos));
        ValidarMonto(tarifa.ImporteBaseUyu, ReglasTarifario.MoneyScale, ReglasTarifario.MaxMoney, nameof(tarifa.ImporteBaseUyu));
        ValidarMonto(tarifa.PrecioPorKilogramoUyu, ReglasTarifario.RateScale, ReglasTarifario.MaxRate, nameof(tarifa.PrecioPorKilogramoUyu));
        ValidarMonto(tarifa.PrecioPorMetroCubicoUyu, ReglasTarifario.RateScale, ReglasTarifario.MaxRate, nameof(tarifa.PrecioPorMetroCubicoUyu));
        ArgumentNullException.ThrowIfNull(tarifa.Ajustes);

        checked
        {
            var totalGramos = 0m;
            var totalCubicCentimeters = 0m;
            foreach (var parcel in bultos)
            {
                ArgumentNullException.ThrowIfNull(parcel);
                if (parcel.PesoGramos is <= 0 or > ReglasTarifario.MaxBultoMeasure
                    || parcel.LargoCentimetros is <= 0 or > ReglasTarifario.MaxBultoMeasure
                    || parcel.AnchoCentimetros is <= 0 or > ReglasTarifario.MaxBultoMeasure
                    || parcel.AltoCentimetros is <= 0 or > ReglasTarifario.MaxBultoMeasure)
                    throw new ArgumentOutOfRangeException(nameof(bultos), "El peso y las dimensiones deben ser positivos y estar dentro de la precisión de Bulto.");
                ValidarMonto(parcel.PesoGramos, 2, ReglasTarifario.MaxBultoMeasure, nameof(bultos));
                ValidarMonto(parcel.LargoCentimetros, 2, ReglasTarifario.MaxBultoMeasure, nameof(bultos));
                ValidarMonto(parcel.AnchoCentimetros, 2, ReglasTarifario.MaxBultoMeasure, nameof(bultos));
                ValidarMonto(parcel.AltoCentimetros, 2, ReglasTarifario.MaxBultoMeasure, nameof(bultos));
                totalGramos += parcel.PesoGramos;
                totalCubicCentimeters += parcel.LargoCentimetros * parcel.AnchoCentimetros * parcel.AltoCentimetros;
            }

            var totalKilograms = totalGramos / ReglasTarifario.GramsPerKilogram;
            var totalCubicMeters = totalCubicCentimeters / ReglasTarifario.CubicCentimetersPerCubicMeter;
            var byWeight = totalKilograms * tarifa.PrecioPorKilogramoUyu;
            var byVolume = totalCubicMeters * tarifa.PrecioPorMetroCubicoUyu;
            var subtotal = tarifa.ImporteBaseUyu + byWeight + byVolume;
            var applied = new List<AjusteTarifarioAplicado>(tarifa.Ajustes.Count);
            var surcharges = 0m;
            var bonuses = 0m;
            foreach (var unvalidated in tarifa.Ajustes)
            {
                ArgumentNullException.ThrowIfNull(unvalidated);
                var adjustment = unvalidated.Validar();
                var percentage = adjustment.Tipo is TipoAjusteTarifario.RecargoPorcentual or TipoAjusteTarifario.BonificacionPorcentual;
                var amount = percentage ? subtotal * adjustment.Valor / 100m : adjustment.Valor;
                var basis = percentage ? BaseAjusteTarifario.SubtotalOriginal : BaseAjusteTarifario.ImporteFijo;
                if (adjustment.Tipo is TipoAjusteTarifario.RecargoFijo or TipoAjusteTarifario.RecargoPorcentual)
                    surcharges += amount;
                else
                    bonuses += amount;
                applied.Add(new(adjustment.Nombre, adjustment.Tipo, basis, adjustment.Valor, amount));
            }
            var exact = subtotal + surcharges - bonuses;
            var floored = Math.Max(0m, exact);
            var final = decimal.Round(floored, ReglasTarifario.FinalMoneyScale, ReglasTarifario.FinalRounding);
            return new(tarifa.ZonaId, tarifa.Modalidad, tarifa.VersionTarifarioId,
                tarifa.NumeroVersionPublicada, tarifa.VersionConcurrencia, tarifa.Moneda,
                totalKilograms, totalCubicMeters, tarifa.ImporteBaseUyu, byWeight, byVolume,
                subtotal, applied, surcharges, bonuses, exact, final, exact < 0m);
        }
    }

    private static void ValidarMonto(decimal value, int scale, decimal maximum, string parameterName)
    {
        if (value < 0 || value > maximum || decimal.Round(value, scale) != value)
            throw new ArgumentOutOfRangeException(parameterName, "El importe está fuera del rango o precisión permitidos.");
    }
}
