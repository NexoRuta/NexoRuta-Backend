namespace NexoRuta.Application.Envios.Exceptions;

public sealed class GeocodificacionNoDisponibleException : Exception
{
    public GeocodificacionNoDisponibleException() : base("No se pudo consultar la dirección. Podés corregir y confirmar el punto manualmente o volver a intentar más tarde.") { }
}
