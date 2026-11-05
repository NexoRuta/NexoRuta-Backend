namespace NexoRuta.Api.Features.Envios.Contracts.Responses;

public sealed record BultoDetalle(
    string Codigo,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros)
{
    public string Resultado { get; init; } = "Pendiente";
    public string? MotivoNoEntrega { get; init; }
    public string Custodia { get; init; } = string.Empty;
}
