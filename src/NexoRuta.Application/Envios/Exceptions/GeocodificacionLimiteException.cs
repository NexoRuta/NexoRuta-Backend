namespace NexoRuta.Application.Envios.Exceptions;

public sealed class GeocodificacionLimiteException(bool cuotaAgotada) : Exception(cuotaAgotada
    ? "Se agotó la cuota de búsqueda de direcciones. Podés seleccionar el punto manualmente en el mapa."
    : "Se alcanzó el límite temporal de búsqueda. Esperá un momento o seleccioná el punto en el mapa.")
{
    public bool CuotaAgotada { get; } = cuotaAgotada;
}
