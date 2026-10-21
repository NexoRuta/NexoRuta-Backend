namespace NexoRuta.Application.Reparto.Dtos;

public sealed record ConfiguracionRepartoDatos(long Version, string ZonaHoraria, int MaximoParadas,
    bool RequiereFotoIntento, bool BloquearDiscrepanciasCarga);
