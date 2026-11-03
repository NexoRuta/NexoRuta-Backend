namespace NexoRuta.Domain.Envios;

// Commercial state edges only. Operation-specific permissions, custody, proofs and
// optimistic versions remain mandatory at their existing domain/API entry points.
public static class TransicionesEstadoEnvio
{
    public static bool Permite(EstadoEnvio origen, EstadoEnvio destino) => (origen, destino) switch
    {
        (EstadoEnvio.Admitido, EstadoEnvio.EnDeposito or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnTransito, EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion or EstadoEnvio.NoEntregado or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.NoEntregado, EstadoEnvio.Reprogramado or EstadoEnvio.EnDevolucion or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.Reprogramado, EstadoEnvio.AsignadoRuta or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnDevolucion, EstadoEnvio.Devuelto or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EntregaPendienteValidacion, EstadoEnvio.Entregado or EstadoEnvio.EntregaSinValidacion) => true,
        _ => false
    };
}
