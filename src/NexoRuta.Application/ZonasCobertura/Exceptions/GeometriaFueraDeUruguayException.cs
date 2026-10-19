namespace NexoRuta.Application.ZonasCobertura.Exceptions;

public sealed class GeometriaFueraDeUruguayException()
    : ArgumentException("La ubicación o geometría excede el ámbito territorial de referencia de Uruguay.");
