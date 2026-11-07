namespace NexoRuta.Application.Operacion.Exceptions;

public sealed class HistorialOperativoNoDisponibleException(string message = "El historial operativo está temporalmente indisponible.", Exception? innerException = null)
    : Exception(message, innerException);
