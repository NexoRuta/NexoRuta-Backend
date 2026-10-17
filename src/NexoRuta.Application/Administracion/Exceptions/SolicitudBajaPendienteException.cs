namespace NexoRuta.Application.Administracion.Exceptions;

public sealed class SolicitudBajaPendienteException()
    : Exception("Ya existe una solicitud de baja pendiente para esta organización.");
