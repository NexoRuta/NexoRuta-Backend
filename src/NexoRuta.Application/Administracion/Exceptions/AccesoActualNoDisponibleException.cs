namespace NexoRuta.Application.Administracion.Exceptions;

public sealed class AccesoActualNoDisponibleException() : Exception(
    "El usuario actual no tiene una pertenencia válida a un operador o a un comercio.");
