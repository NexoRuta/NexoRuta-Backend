namespace NexoRuta.Domain.Envios;

public enum EstadoEnvio
{
    Admitido,
    EnDeposito,
    AsignadoRuta,
    EnTransito,
    Entregado,
    NoEntregado,
    Reprogramado,
    EnDevolucion,
    Devuelto,
    Extraviado,
    EntregaPendienteValidacion,
    EntregaSinValidacion,
    EntregadoParcialmente,
    RetiroSolicitado, RetiroAsignado, Retirado, RetiroFallido, RetiroEnDeposito, EntregadoComercio,
    Cancelado
}
