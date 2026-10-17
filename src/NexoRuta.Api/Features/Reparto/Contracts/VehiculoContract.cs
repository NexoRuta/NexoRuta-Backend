using System.ComponentModel.DataAnnotations;
using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed class VehiculoContract
{
    public Guid Id { get; init; }
    [Required, StringLength(20)] public string Matricula { get; init; } = "";
    [Range(typeof(decimal), "0.01", "9999999999999999", ParseLimitsInInvariantCulture = true)] public decimal PesoMaximoGramos { get; init; }
    [Range(typeof(decimal), "0.01", "9999999999999999999999", ParseLimitsInInvariantCulture = true)] public decimal VolumenMaximoCm3 { get; init; }
    public bool Activo { get; init; } = true;
    [Range(0, long.MaxValue)] public long Version { get; init; }
    public VehiculoDatos Datos() => new(Id, Matricula, PesoMaximoGramos, VolumenMaximoCm3, Activo, Version);
    public static VehiculoContract Desde(VehiculoDatos x) => new()
    { Id = x.Id, Matricula = x.Matricula, PesoMaximoGramos = x.PesoMaximoGramos, VolumenMaximoCm3 = x.VolumenMaximoCm3, Activo = x.Activo, Version = x.Version };
}
