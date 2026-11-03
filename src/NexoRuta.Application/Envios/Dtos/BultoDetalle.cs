namespace NexoRuta.Application.Envios.Dtos;

public sealed record BultoDetalle(
    Guid Id,
    string Codigo,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros)
{
    public string Custodia { get; init; } = string.Empty;
}
