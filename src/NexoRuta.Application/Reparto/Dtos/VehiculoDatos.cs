namespace NexoRuta.Application.Reparto.Dtos;

public sealed record VehiculoDatos(Guid Id, string Matricula, decimal PesoMaximoGramos, decimal VolumenMaximoCm3, bool Activo, long Version);
