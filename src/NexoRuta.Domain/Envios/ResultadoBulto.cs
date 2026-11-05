namespace NexoRuta.Domain.Envios;

public enum ResultadoBulto { Pendiente, Entregado, EntregaPendienteValidacion, EntregaSinValidacion, EnDevolucion, Devuelto }

public static class TransicionesResultadoBulto
{
    public static bool Permite(ResultadoBulto origen, ResultadoBulto destino) => (origen, destino) switch
    {
        (ResultadoBulto.Pendiente, ResultadoBulto.Entregado or ResultadoBulto.EntregaPendienteValidacion or ResultadoBulto.EnDevolucion) => true,
        (ResultadoBulto.EntregaPendienteValidacion, ResultadoBulto.Entregado or ResultadoBulto.EntregaSinValidacion) => true,
        (ResultadoBulto.EnDevolucion, ResultadoBulto.Devuelto) => true,
        _ => false
    };
    public static bool EntregaFisica(ResultadoBulto estado) => estado is ResultadoBulto.Entregado or ResultadoBulto.EntregaPendienteValidacion or ResultadoBulto.EntregaSinValidacion;
}

public static class AgregacionBultos
{
    // Ordered rules: physical delivery with outstanding parcels stays partial, including delivered+returned.
    public static EstadoEnvio Derivar(IReadOnlyCollection<ResultadoBulto> resultados, EstadoEnvio sinEntregas)
    {
        if (resultados.Count == 0) throw new ArgumentException("El envío requiere bultos.");
        return resultados switch
        {
            _ when resultados.All(x => x == ResultadoBulto.Entregado) => EstadoEnvio.Entregado,
            _ when resultados.All(TransicionesResultadoBulto.EntregaFisica) && resultados.Contains(ResultadoBulto.EntregaSinValidacion) => EstadoEnvio.EntregaSinValidacion,
            _ when resultados.All(TransicionesResultadoBulto.EntregaFisica) => EstadoEnvio.EntregaPendienteValidacion,
            _ when resultados.Any(TransicionesResultadoBulto.EntregaFisica) => EstadoEnvio.EntregadoParcialmente,
            _ when resultados.All(x => x == ResultadoBulto.Devuelto) => EstadoEnvio.Devuelto,
            _ when resultados.Any(x => x == ResultadoBulto.EnDevolucion) => EstadoEnvio.EnDevolucion,
            _ => sinEntregas
        };
    }
}
