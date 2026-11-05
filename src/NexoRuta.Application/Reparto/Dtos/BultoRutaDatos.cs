namespace NexoRuta.Application.Reparto.Dtos;

public sealed record BultoRutaDatos(Guid Id, string Codigo, decimal PesoGramos, decimal VolumenCm3,
    string Custodia, long Version) { public bool EnEstaRuta { get; init; } public string Resultado { get; init; } = "Pendiente"; }
