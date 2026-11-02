using NexoRuta.Domain.Reparto;
namespace NexoRuta.UnitTests.Reparto;
public sealed class PlanTemporalTests
{
    [Fact]
    public void DefaultServiceIsCapturedAndManualChronologyIncludesServiceAcrossMidnight()
    {
        var op = Guid.NewGuid();
        var basis = new BaseOperativa(op, "A", 20, 7);
        var sheet = new HojaRuta(op, Guid.NewGuid(), new Vehiculo(op, "ABC", 1000, 1000), new(2026, 11, 1), 1);
        var first = DateTimeOffset.Parse("2026-11-02T02:55:00Z");
        sheet.AgregarParada(Guid.NewGuid(), first, 20);
        sheet.CapturarPlanTemporal(basis, first.AddMinutes(-10), "America/Montevideo");
        basis.Actualizar("B", 1, 2);
        var version = sheet.Version;
        Assert.Throws<ArgumentException>(() => sheet.AgregarParada(Guid.NewGuid(), first.AddMinutes(19)));
        Assert.Equal(version, sheet.Version); Assert.Single(sheet.Paradas);
        sheet.AgregarParada(Guid.NewGuid(), first.AddMinutes(20));
        Assert.Equal(20, sheet.Paradas[1].ServicioMinutos);
        Assert.Equal("A", sheet.PlanTemporal!.DireccionBase);
        Assert.Single(sheet.PlanTemporal.OrdenEnvios);
        Assert.Equal(version, sheet.PlanTemporal.VersionRutaAlPlanificar);
        Assert.Throws<InvalidOperationException>(() => sheet.CapturarPlanTemporal(basis, null, "UTC"));
        Assert.Null(sheet.SalidaEnUtc);
    }
    [Fact]
    public void InvalidPointOrNegativeTimeDoesNotMutateConfirmation()
    {
        var basis = new BaseOperativa(Guid.NewGuid(), "A", null, null);
        Assert.Throws<ArgumentException>(() => basis.Confirmar(double.NaN, 0, Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => basis.Actualizar("B", -1, 0));
        Assert.Equal("A", basis.Direccion); Assert.Null(basis.ConfirmadaEnUtc);
        basis.Confirmar(0, 0, Guid.NewGuid(), DateTimeOffset.UtcNow);
        basis.Actualizar(" A ", 0, 0); Assert.NotNull(basis.ConfirmadaEnUtc);
        basis.Actualizar("B", null, null); Assert.Null(basis.Latitud); Assert.Null(basis.ConfirmadaPorAccesoId);
    }
}
