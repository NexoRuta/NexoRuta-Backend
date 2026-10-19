namespace NexoRuta.Domain.Tarifas;

public sealed class TarifaZonaInvalidaException()
    : InvalidOperationException("Una o más zonas no existen o no pertenecen al operador activo.");
