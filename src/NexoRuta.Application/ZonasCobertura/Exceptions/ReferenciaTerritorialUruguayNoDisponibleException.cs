namespace NexoRuta.Application.ZonasCobertura.Exceptions;

public sealed class ReferenciaTerritorialUruguayNoDisponibleException()
    : InvalidOperationException("La referencia territorial de Uruguay no está disponible. Reintentá más tarde.");
