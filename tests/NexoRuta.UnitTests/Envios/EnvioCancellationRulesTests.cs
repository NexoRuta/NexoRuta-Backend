using NexoRuta.Domain.Envios;

namespace NexoRuta.UnitTests.Envios;

public sealed class EnvioCancellationRulesTests
{
    [Theory]
    [InlineData(EstadoEnvio.Admitido)]
    [InlineData(EstadoEnvio.EnDeposito)]
    [InlineData(EstadoEnvio.AsignadoRuta)]
    [InlineData(EstadoEnvio.EnTransito)]
    [InlineData(EstadoEnvio.NoEntregado)]
    [InlineData(EstadoEnvio.Reprogramado)]
    [InlineData(EstadoEnvio.EnDevolucion)]
    public void PermiteCancelarDesdeLosSieteEstadosAutorizados(EstadoEnvio origen)
    {
        var envio = CrearEn(origen);

        envio.CambiarEstado(EstadoEnvio.Cancelado, envio.Version);

        Assert.Equal(EstadoEnvio.Cancelado, envio.Estado);
    }

    [Theory]
    [InlineData(EstadoEnvio.Entregado)]
    [InlineData(EstadoEnvio.Devuelto)]
    [InlineData(EstadoEnvio.Extraviado)]
    [InlineData(EstadoEnvio.EntregaPendienteValidacion)]
    [InlineData(EstadoEnvio.EntregaSinValidacion)]
    public void RechazaCancelarDesdeLosCincoEstadosProhibidos(EstadoEnvio origen)
    {
        var envio = CrearEn(origen);
        var version = envio.Version;

        Assert.Throws<InvalidOperationException>(() => envio.CambiarEstado(EstadoEnvio.Cancelado, version));

        Assert.Equal(origen, envio.Estado);
        Assert.Equal(version, envio.Version);
    }

    [Theory]
    [InlineData(EstadoEnvio.Admitido)]
    [InlineData(EstadoEnvio.EnDeposito)]
    [InlineData(EstadoEnvio.AsignadoRuta)]
    [InlineData(EstadoEnvio.EnTransito)]
    [InlineData(EstadoEnvio.Entregado)]
    [InlineData(EstadoEnvio.NoEntregado)]
    [InlineData(EstadoEnvio.Reprogramado)]
    [InlineData(EstadoEnvio.EnDevolucion)]
    [InlineData(EstadoEnvio.Devuelto)]
    [InlineData(EstadoEnvio.Extraviado)]
    [InlineData(EstadoEnvio.EntregaPendienteValidacion)]
    [InlineData(EstadoEnvio.EntregaSinValidacion)]
    [InlineData(EstadoEnvio.Cancelado)]
    public void CanceladoNoPuedeCambiarAOtroEstado(EstadoEnvio destino)
    {
        var envio = CrearEn(EstadoEnvio.Admitido);
        envio.CambiarEstado(EstadoEnvio.Cancelado, envio.Version);
        var version = envio.Version;

        Assert.Throws<InvalidOperationException>(() => envio.CambiarEstado(destino, version));

        Assert.Equal(EstadoEnvio.Cancelado, envio.Estado);
        Assert.Equal(version, envio.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConciliarEntregaTardiaRegistraElHechoSinReactivarCancelado(bool pruebaCompleta)
    {
        var envio = CrearEn(EstadoEnvio.Admitido);
        envio.CambiarEstado(EstadoEnvio.Cancelado, envio.Version);
        var version = envio.Version;
        var instante = DateTimeOffset.Parse("2026-10-24T12:00:00Z");

        envio.ConciliarEntregaFisica(instante, instante.AddMinutes(1), null, pruebaCompleta, version);

        Assert.Equal(EstadoEnvio.Cancelado, envio.Estado);
        Assert.Equal(instante, envio.EntregaFisicaEnUtc);
        Assert.Equal(version + 1, envio.Version);
    }

    [Fact]
    public void PrimeraRecepcionFijaElVencimientoYLaPruebaEnElInstanteLimiteNoCompleta()
    {
        var envio = CrearEn(EstadoEnvio.EnTransito);
        var fisica = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
        var recepcion = DateTimeOffset.Parse("2026-10-24T12:05:00Z");

        envio.DeclararEntregaFisica(fisica, recepcion, 60, false, envio.Version);
        var version = envio.Version;

        Assert.Equal(fisica, envio.EntregaFisicaEnUtc);
        Assert.Equal(recepcion, envio.EntregaFisicaRecibidaEnUtc);
        Assert.Equal(recepcion.AddMinutes(60), envio.PlazoPruebaVenceEnUtc);
        Assert.False(envio.RegistrarEvidenciaEntrega(recepcion.AddMinutes(60), true, version));
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, envio.Estado);
        Assert.Equal(version + 1, envio.Version);
        Assert.Equal(recepcion.AddMinutes(60), envio.PlazoPruebaVenceEnUtc);
    }

    [Fact]
    public void PruebaCompletaAntesDelLimiteValidaLaEntrega()
    {
        var envio = CrearEn(EstadoEnvio.EnTransito);
        var recepcion = DateTimeOffset.Parse("2026-10-24T12:05:00Z");
        envio.DeclararEntregaFisica(recepcion.AddMinutes(-1), recepcion, 60, false, envio.Version);

        Assert.True(envio.RegistrarEvidenciaEntrega(recepcion.AddMinutes(59), true, envio.Version));
        Assert.Equal(EstadoEnvio.Entregado, envio.Estado);
        Assert.Equal(recepcion.AddMinutes(60), envio.PlazoPruebaVenceEnUtc);
    }

    [Fact]
    public void UnNuevoIntentoDesdeNoEntregadoRequiereReprogramacion()
    {
        var envio = CrearEn(EstadoEnvio.NoEntregado);
        var version = envio.Version;

        Assert.Throws<InvalidOperationException>(() => envio.CambiarEstado(EstadoEnvio.EnTransito, version));
        Assert.Equal(EstadoEnvio.NoEntregado, envio.Estado);
        Assert.Equal(version, envio.Version);

        envio.CambiarEstado(EstadoEnvio.Reprogramado, version);
        Assert.Equal(EstadoEnvio.Reprogramado, envio.Estado);
    }

    private static Envio CrearEn(EstadoEnvio estado)
    {
        var envio = new Envio(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        EstadoEnvio[] destino = estado switch
        {
            EstadoEnvio.Admitido => [],
            EstadoEnvio.EnDeposito => [EstadoEnvio.EnDeposito],
            EstadoEnvio.AsignadoRuta => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta],
            EstadoEnvio.EnTransito => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito],
            EstadoEnvio.NoEntregado => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito, EstadoEnvio.NoEntregado],
            EstadoEnvio.Reprogramado => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito, EstadoEnvio.NoEntregado, EstadoEnvio.Reprogramado],
            EstadoEnvio.EnDevolucion => [EstadoEnvio.EnDeposito, EstadoEnvio.EnDevolucion],
            EstadoEnvio.Devuelto => [EstadoEnvio.EnDeposito, EstadoEnvio.EnDevolucion, EstadoEnvio.Devuelto],
            EstadoEnvio.Extraviado => [EstadoEnvio.EnDeposito, EstadoEnvio.Extraviado],
            EstadoEnvio.Entregado => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito],
            EstadoEnvio.EntregaPendienteValidacion => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito],
            EstadoEnvio.EntregaSinValidacion => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito],
            _ => throw new ArgumentOutOfRangeException(nameof(estado), estado, "El estado de prueba no se puede construir.")
        };

        foreach (var siguiente in destino)
        {
            envio.CambiarEstado(siguiente, envio.Version);
        }

        if (estado is EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion)
        {
            var instant = DateTimeOffset.UtcNow;
            envio.DeclararEntregaFisica(instant, instant, null, estado == EstadoEnvio.Entregado, envio.Version);
        }
        else if (estado == EstadoEnvio.EntregaSinValidacion)
        {
            var instant = DateTimeOffset.UtcNow;
            envio.DeclararEntregaFisica(instant, instant, null, false, envio.Version);
            envio.CambiarEstado(EstadoEnvio.EntregaSinValidacion, envio.Version);
        }

        return envio;
    }
}
