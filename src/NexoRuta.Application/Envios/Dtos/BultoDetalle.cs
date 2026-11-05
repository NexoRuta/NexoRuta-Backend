namespace NexoRuta.Application.Envios.Dtos;

public sealed record BultoDetalle(
    Guid Id,
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
