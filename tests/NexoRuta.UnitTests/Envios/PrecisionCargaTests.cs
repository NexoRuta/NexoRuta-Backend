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
