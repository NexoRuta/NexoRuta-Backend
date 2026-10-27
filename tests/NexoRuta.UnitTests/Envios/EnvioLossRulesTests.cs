using NexoRuta.Domain.Envios;

namespace NexoRuta.UnitTests.Envios;

public sealed class EnvioLossRulesTests
{
    [Theory]
    [InlineData(EstadoEnvio.EnDeposito)]
    [InlineData(EstadoEnvio.AsignadoRuta)]
    [InlineData(EstadoEnvio.EnTransito)]
    [InlineData(EstadoEnvio.EnDevolucion)]
    public void ConfirmationCanMoveOnlyFromFourAllowedStates(EstadoEnvio origin)
    {
        var shipment = CreateAt(origin);
        var before = shipment.Version;

        shipment.ConfirmarExtravio(before);

        Assert.Equal(EstadoEnvio.Extraviado, shipment.Estado);
        Assert.Equal(before + 1, shipment.Version);
    }

    [Theory]
    [InlineData(EstadoEnvio.Admitido)]
    [InlineData(EstadoEnvio.Entregado)]
    [InlineData(EstadoEnvio.Devuelto)]
    [InlineData(EstadoEnvio.Extraviado)]
    [InlineData(EstadoEnvio.EntregaPendienteValidacion)]
    [InlineData(EstadoEnvio.EntregaSinValidacion)]
    [InlineData(EstadoEnvio.NoEntregado)]
    [InlineData(EstadoEnvio.Reprogramado)]
    [InlineData(EstadoEnvio.Cancelado)]
    public void ConfirmationIsRejectedFromEveryOtherState(EstadoEnvio origin)
    {
        var shipment = CreateAt(origin);
        var before = shipment.Version;

        Assert.Throws<InvalidOperationException>(() => shipment.ConfirmarExtravio(before));

        Assert.Equal(origin, shipment.Estado);
        Assert.Equal(before, shipment.Version);
    }

    private static Envio CreateAt(EstadoEnvio state)
    {
        var shipment = new Envio(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        EstadoEnvio[] path = state switch
        {
            EstadoEnvio.Admitido => [],
            EstadoEnvio.EnDeposito => [EstadoEnvio.EnDeposito],
            EstadoEnvio.AsignadoRuta => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta],
            EstadoEnvio.EnTransito => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito],
            EstadoEnvio.NoEntregado => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito, EstadoEnvio.NoEntregado],
            EstadoEnvio.Reprogramado => [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito,
                EstadoEnvio.NoEntregado, EstadoEnvio.Reprogramado],
            EstadoEnvio.EnDevolucion => [EstadoEnvio.EnDeposito, EstadoEnvio.EnDevolucion],
            EstadoEnvio.Devuelto => [EstadoEnvio.EnDeposito, EstadoEnvio.EnDevolucion, EstadoEnvio.Devuelto],
            EstadoEnvio.Extraviado => [EstadoEnvio.EnDeposito, EstadoEnvio.Extraviado],
            EstadoEnvio.Cancelado => [EstadoEnvio.EnDeposito, EstadoEnvio.Cancelado],
            EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion or EstadoEnvio.EntregaSinValidacion =>
                [EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito],
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
        foreach (var next in path) shipment.CambiarEstado(next, shipment.Version);

        if (state is EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion or EstadoEnvio.EntregaSinValidacion)
        {
            var now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
            shipment.DeclararEntregaFisica(now, now, null, state == EstadoEnvio.Entregado, shipment.Version);
            if (state == EstadoEnvio.EntregaSinValidacion)
                shipment.CambiarEstado(EstadoEnvio.EntregaSinValidacion, shipment.Version);
        }
        return shipment;
    }
}
