namespace NexoRuta.Application.Reparto.Estimaciones;

public sealed class AdmisionGpsOptions
{
    public int AntiguedadMaximaSegundos { get; init; } = 120;
    public double PrecisionMaximaMetros { get; init; } = 100;
    // No existing journey contract authorizes device clock lead: reject every future measurement.
    public static readonly TimeSpan ToleranciaFutura = TimeSpan.Zero;
}
public sealed record PosicionProyeccion(Guid Id, PuntoVial Punto, DateTimeOffset MedidaEnUtc,
    DateTimeOffset RecibidaEnUtc, double PrecisionMetros);
public sealed record SeleccionPosicion(PosicionProyeccion? Posicion, string Motivo);
public static class SelectorPosicion
{
    public static SeleccionPosicion Elegir(IEnumerable<PosicionProyeccion> posiciones, DateTimeOffset ahora,
        DateTimeOffset inicioEjecucion, AdmisionGpsOptions opciones,DateTimeOffset? minimaProyeccion = null)
    {
        string Motivo(PosicionProyeccion p) => p.Id == Guid.Empty || p.Punto?.EsValido != true
            || !double.IsFinite(p.PrecisionMetros) || p.PrecisionMetros <= 0 || p.MedidaEnUtc == default
            || p.RecibidaEnUtc == default ? "GpsInvalido"
            : p.MedidaEnUtc > ahora + AdmisionGpsOptions.ToleranciaFutura ? "GpsFuturo"
            : p.MedidaEnUtc < inicioEjecucion ? "GpsOtraEjecucion"
            : p.MedidaEnUtc < minimaProyeccion ? "GpsAnteriorProyeccion"
            : ahora - p.MedidaEnUtc > TimeSpan.FromSeconds(opciones.AntiguedadMaximaSegundos) ? "GpsAntiguo"
            : p.PrecisionMetros > opciones.PrecisionMaximaMetros ? "GpsImpreciso" : "Admitida";
        var candidatas=posiciones.OrderByDescending(p=>p.MedidaEnUtc).ThenBy(p=>p.PrecisionMetros).ThenBy(p=>p.Id).ToArray();
        var elegida=candidatas.FirstOrDefault(p=>Motivo(p)=="Admitida");
        return elegida is not null ? new(elegida,"Admitida") : new(null,candidatas.Length==0?"SinPosicion":Motivo(candidatas[0]));
    }
}
