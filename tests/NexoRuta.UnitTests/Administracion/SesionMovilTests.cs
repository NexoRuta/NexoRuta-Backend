using NexoRuta.Domain.Administracion;

namespace NexoRuta.UnitTests.Administracion;

public sealed class SesionMovilTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void RenovarNoExtiendeElVencimientoAbsoluto()
    {
        var sesion = Nueva();
        Assert.True(sesion.AutorizarRenovacion(false, Inicio.AddDays(29)));
        Assert.Equal(Inicio.AddDays(30), sesion.ExpiraEnUtc);
        Assert.Equal(TimeSpan.Zero, sesion.CreadaEnUtc.Offset);
        Assert.Equal(TimeSpan.Zero, sesion.ExpiraEnUtc.Offset);
        Assert.False(sesion.AutorizarRenovacion(false, Inicio.AddDays(30)));
    }

    [Fact]
    public void ContratoUtcConservaPrecisionDeMicrosegundosAntesDePersistir()
    {
        var ahora = Inicio.AddTicks(7);
        var sesion = new SesionMovil(Guid.NewGuid(), Guid.NewGuid(), 0, 0, ahora, ahora.AddDays(30));
        Assert.Equal(0, sesion.CreadaEnUtc.UtcTicks % 10);
        Assert.Equal(0, sesion.ExpiraEnUtc.UtcTicks % 10);
        Assert.Equal(Inicio.AddDays(30), sesion.ExpiraEnUtc);
    }

    [Fact]
    public void ReutilizarTokenConsumidoRevocaTodaLaSesionSinReabrirla()
    {
        var sesion = Nueva();
        Assert.False(sesion.AutorizarRenovacion(true, Inicio.AddMinutes(1)));
        Assert.False(sesion.EstaVigente(Inicio.AddMinutes(2)));
        Assert.False(sesion.AutorizarRenovacion(false, Inicio.AddMinutes(2)));
        sesion.Revocar(Inicio.AddMinutes(3));
        Assert.Equal(Inicio.AddMinutes(1), sesion.RevocadaEnUtc);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void VencimientoNoPositivoNoCreaSesion(int dias)
        => Assert.Throws<ArgumentException>(() => new SesionMovil(Guid.NewGuid(), Guid.NewGuid(), 0, 0, Inicio, Inicio.AddDays(dias)));

    [Fact]
    public void IdentidadAccesoOVersionInvalidosSeRechazan()
    {
        Assert.Throws<ArgumentException>(() => new SesionMovil(Guid.Empty, Guid.NewGuid(), 0, 0, Inicio, Inicio.AddDays(30)));
        Assert.Throws<ArgumentException>(() => new SesionMovil(Guid.NewGuid(), Guid.Empty, 0, 0, Inicio, Inicio.AddDays(30)));
        Assert.Throws<ArgumentException>(() => new SesionMovil(Guid.NewGuid(), Guid.NewGuid(), -1, 0, Inicio, Inicio.AddDays(30)));
    }

    private static SesionMovil Nueva() => new(Guid.NewGuid(), Guid.NewGuid(), 7, 0, Inicio, Inicio.AddDays(30));
}
