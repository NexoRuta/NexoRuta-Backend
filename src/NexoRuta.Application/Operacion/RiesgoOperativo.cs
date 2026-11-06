namespace NexoRuta.Application.Operacion;

public enum RiesgoOperativo
{
    ProyeccionAtrasada, Finalizado, SinCompromiso, SinEstimacion, PlazoIncumplido, EnRiesgo, SinRiesgoDetectado
}

public static class EvaluadorRiesgoOperativo
{
    public static RiesgoOperativo Evaluar(DateTimeOffset ahora, bool proyeccionActualizada, bool finalizado,
        DateTimeOffset? compromisoHasta, DateTimeOffset? llegadaEstimada)
    {
        if (!proyeccionActualizada) return RiesgoOperativo.ProyeccionAtrasada;
        if (finalizado) return RiesgoOperativo.Finalizado;
        if (compromisoHasta is null) return RiesgoOperativo.SinCompromiso;
        if (ahora >= compromisoHasta) return RiesgoOperativo.PlazoIncumplido;
        if (llegadaEstimada is null || llegadaEstimada < ahora) return RiesgoOperativo.SinEstimacion;
        return llegadaEstimada > compromisoHasta ? RiesgoOperativo.EnRiesgo : RiesgoOperativo.SinRiesgoDetectado;
    }
}
