namespace NexoRuta.Application.Envios.Dtos;

public sealed record BultoDetalle(
    string Codigo,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros);
