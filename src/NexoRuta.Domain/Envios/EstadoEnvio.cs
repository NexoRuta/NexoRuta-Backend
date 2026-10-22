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
    Cancelado
}
