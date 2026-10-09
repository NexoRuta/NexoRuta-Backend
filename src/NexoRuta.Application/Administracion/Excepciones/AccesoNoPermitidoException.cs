namespace NexoRuta.Application.Administracion.Excepciones;

public sealed class AccesoNoPermitidoException() : Exception("Solo los usuarios de comercio pueden dar de alta envíos.");
