using NexoRuta.Domain.Envios;

namespace NexoRuta.UnitTests.Envios;

public sealed class EnvioStateMachineTests
{
    private static readonly (EstadoEnvio From, EstadoEnvio To)[] Allowed =
    [
        (EstadoEnvio.Admitido, EstadoEnvio.EnDeposito),
        (EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta),
        (EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito),
        (EstadoEnvio.EnTransito, EstadoEnvio.Entregado),
        (EstadoEnvio.EnTransito, EstadoEnvio.EntregaPendienteValidacion),
        (EstadoEnvio.EntregaPendienteValidacion, EstadoEnvio.Entregado),
        (EstadoEnvio.EntregaPendienteValidacion, EstadoEnvio.EntregaSinValidacion),
        (EstadoEnvio.EnTransito, EstadoEnvio.NoEntregado),
        (EstadoEnvio.NoEntregado, EstadoEnvio.Reprogramado),
        (EstadoEnvio.Reprogramado, EstadoEnvio.AsignadoRuta),
        (EstadoEnvio.NoEntregado, EstadoEnvio.EnDevolucion),
        (EstadoEnvio.EnDevolucion, EstadoEnvio.Devuelto),
        (EstadoEnvio.EnDeposito, EstadoEnvio.Extraviado),
        (EstadoEnvio.AsignadoRuta, EstadoEnvio.Extraviado),
        (EstadoEnvio.EnTransito, EstadoEnvio.Extraviado),
        (EstadoEnvio.EnDevolucion, EstadoEnvio.Extraviado),
        (EstadoEnvio.Admitido, EstadoEnvio.Cancelado),
        (EstadoEnvio.EnDeposito, EstadoEnvio.Cancelado),
        (EstadoEnvio.AsignadoRuta, EstadoEnvio.Cancelado),
        (EstadoEnvio.EnTransito, EstadoEnvio.Cancelado),
        (EstadoEnvio.NoEntregado, EstadoEnvio.Cancelado),
        (EstadoEnvio.Reprogramado, EstadoEnvio.Cancelado),
        (EstadoEnvio.EnDevolucion, EstadoEnvio.Cancelado)
    ];

    [Fact]
    public void CambiarEstadoAceptaSoloLasFlechasConfirmadas()
    {
        var states = Enum.GetValues<EstadoEnvio>();
        foreach (var origin in states)
        foreach (var destination in states)
        {
            var shipment = CreateAt(origin);
            var expected = Allowed.Contains((origin, destination));
            var before = shipment.Version;

            if (expected)
            {
                if (origin == EstadoEnvio.EnTransito && destination is EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion)
                {
                    var now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
                    shipment.DeclararEntregaFisica(now, now, null, destination == EstadoEnvio.Entregado, before);
                }
                else
                {
                    shipment.CambiarEstado(destination, before);
                }

                Assert.Equal(destination, shipment.Estado);
                Assert.Equal(before + 1, shipment.Version);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => shipment.CambiarEstado(destination, before));
                Assert.Equal(origin, shipment.Estado);
                Assert.Equal(before, shipment.Version);
            }
        }
    }

    private static Envio CreateAt(EstadoEnvio state)
    {
        var shipment = new Envio(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        void Move(EstadoEnvio destination) => shipment.CambiarEstado(destination, shipment.Version);

        switch (state)
        {
            case EstadoEnvio.Admitido:
                break;
            case EstadoEnvio.EnDeposito:
                Move(EstadoEnvio.EnDeposito);
                break;
            case EstadoEnvio.AsignadoRuta:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.AsignadoRuta);
                break;
            case EstadoEnvio.EnTransito:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.AsignadoRuta); Move(EstadoEnvio.EnTransito);
                break;
            case EstadoEnvio.NoEntregado:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.AsignadoRuta); Move(EstadoEnvio.EnTransito); Move(EstadoEnvio.NoEntregado);
                break;
            case EstadoEnvio.Reprogramado:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.AsignadoRuta); Move(EstadoEnvio.EnTransito);
                Move(EstadoEnvio.NoEntregado); Move(EstadoEnvio.Reprogramado);
                break;
            case EstadoEnvio.EnDevolucion:
            case EstadoEnvio.Devuelto:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.AsignadoRuta); Move(EstadoEnvio.EnTransito);
                Move(EstadoEnvio.NoEntregado); Move(EstadoEnvio.EnDevolucion);
                if (state == EstadoEnvio.Devuelto) Move(EstadoEnvio.Devuelto);
                break;
            case EstadoEnvio.Extraviado:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.Extraviado);
                break;
            case EstadoEnvio.Entregado:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.AsignadoRuta); Move(EstadoEnvio.EnTransito);
                var delivered = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
                shipment.DeclararEntregaFisica(delivered, delivered, null, true, shipment.Version);
                break;
            case EstadoEnvio.EntregaPendienteValidacion:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.AsignadoRuta); Move(EstadoEnvio.EnTransito);
                var pending = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
                shipment.DeclararEntregaFisica(pending, pending, null, false, shipment.Version);
                break;
            case EstadoEnvio.EntregaSinValidacion:
                Move(EstadoEnvio.EnDeposito); Move(EstadoEnvio.AsignadoRuta); Move(EstadoEnvio.EnTransito);
                var expired = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
                shipment.DeclararEntregaFisica(expired, expired, null, false, shipment.Version);
                Move(EstadoEnvio.EntregaSinValidacion);
                break;
            case EstadoEnvio.Cancelado:
                Move(EstadoEnvio.Cancelado);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        return shipment;
    }
}
