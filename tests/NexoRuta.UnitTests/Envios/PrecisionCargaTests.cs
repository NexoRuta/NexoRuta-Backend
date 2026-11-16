using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;

namespace NexoRuta.UnitTests.Envios;

public sealed class PrecisionCargaTests
{
    [Theory]
    [InlineData("1.005")]
    [InlineData("0.001")]
    public void DomainRejectsExtraSignificantPrecision(string text)
    {
        var value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        Assert.ThrowsAny<ArgumentException>(() => new Vehiculo(Guid.NewGuid(), "ABC", value, 1));
        Assert.ThrowsAny<ArgumentException>(() => new Vehiculo(Guid.NewGuid(), "ABC", 1, value));
        Assert.ThrowsAny<ArgumentException>(() => new Bulto(Guid.NewGuid(), Guid.NewGuid(), "B", value, 1, 1, 1));
        Assert.ThrowsAny<ArgumentException>(() => new Bulto(Guid.NewGuid(), Guid.NewGuid(), "B", 1, value, 1, 1));
    }

    [Fact]
    public void TrailingZerosDoNotChangeEffectivePrecision()
    {
        var v = new Vehiculo(Guid.NewGuid(), " ab-c  d ", 1.000m, 1.00m);
        Assert.Equal("AB-C  D", v.Matricula);
        var b = new Bulto(v.OperadorId, Guid.NewGuid(), "B", 1.000m, 0.01m, 0.01m, 0.01m);
        Assert.Equal(0.000001m, b.LargoCentimetros * b.AnchoCentimetros * b.AltoCentimetros);
    }

    [Theory]
    [InlineData("0.01", true)]
    [InlineData("999999999", true)]
    [InlineData("1.230", true)]
    [InlineData("0.009", false)]
    [InlineData("1000000000", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1.234", false)]
    public void CadaMedidaConservaLimitesPrecisionYExcepciones(string text, bool accepted)
    {
        var value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var fields = new[] { "pesoGramos", "largoCentimetros", "anchoCentimetros", "altoCentimetros" };
        var positiveMessages = new[] { "El peso debe ser positivo.", "El largo debe ser positivo.", "El ancho debe ser positivo.", "El alto debe ser positivo." };
        for (var field = 0; field < fields.Length; field++)
        {
            var measures = new[] { 1m, 1m, 1m, 1m }; measures[field] = value;
            Bulto Create() => new(Guid.NewGuid(), Guid.NewGuid(), "B", measures[0], measures[1], measures[2], measures[3]);
            if (accepted)
            {
                var b = Create();
                var actual = new[] { b.PesoGramos, b.LargoCentimetros, b.AnchoCentimetros, b.AltoCentimetros }[field];
                Assert.Equal(decimal.GetBits(value), decimal.GetBits(actual)); // Sin redondeo ni normalización.
            }
            else if (value <= 0)
            {
                var error = Assert.Throws<ArgumentOutOfRangeException>(() => Create());
                Assert.Equal(fields[field], error.ParamName);
                Assert.StartsWith(positiveMessages[field], error.Message);
            }
            else
            {
                var error = Assert.Throws<ArgumentException>(() => Create());
                Assert.Null(error.ParamName);
                Assert.Equal("Las medidas admiten valores entre 0.01 y 999999999, con hasta dos decimales efectivos.", error.Message);
            }
        }
    }

    [Fact]
    public void LoadRuleDoesNotRoundIndividualVolumesOrCountPackagesTwice()
    {
        var vehicle = new Vehiculo(Guid.NewGuid(), "ABC", 1, 0.01m);
        var b = new Bulto(vehicle.OperadorId, Guid.NewGuid(), "B", 1, 0.01m, 0.01m, 0.01m);
        Assert.Null(CapacidadCarga.Exceso(vehicle, new[] { b, b }));
        var tiny = Enumerable.Range(0, 101).Select(i => new Bulto(vehicle.OperadorId, Guid.NewGuid(), $"B{i}", 0.01m, 0.01m, 0.01m, 1));
        var volumeOnly = new Vehiculo(vehicle.OperadorId, "VOL", 100, 0.01m);
        Assert.Contains("volumen", CapacidadCarga.Exceso(volumeOnly, tiny)!);
    }
}
