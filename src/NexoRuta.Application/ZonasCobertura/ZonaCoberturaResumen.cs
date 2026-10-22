using NexoRuta.Domain.ZonasCobertura;

namespace NexoRuta.Application.ZonasCobertura;

public sealed record ZonaCoberturaResumen(
    Guid Id,
    string Nombre,
    EstadoZonaCobertura Estado,
    int FranjasActivas,
    long Version,
    int RevisionActual,
    DateTimeOffset ActualizadaUtc);
