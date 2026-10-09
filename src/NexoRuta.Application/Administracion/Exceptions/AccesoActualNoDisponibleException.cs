namespace NexoRuta.Application.Administracion.Exceptions;

public sealed class AccesoActualNoDisponibleException() : Exception(
    "El usuario actual no tiene un acceso válido a un operador y comercio.");
