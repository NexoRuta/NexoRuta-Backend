namespace NexoRuta.Application.Administracion.Exceptions;

public sealed class OrganizacionSinDuenoException()
    : Exception("La organización no tiene un dueño registrado. La edición está deshabilitada hasta que se resuelva su propiedad.");
