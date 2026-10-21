using System.ComponentModel.DataAnnotations;
using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed class ConfiguracionRepartoContract
{
    [Range(0, long.MaxValue)] public long Version { get; init; }
    [Required, StringLength(100)] public string ZonaHoraria { get; init; } = "";
    [Range(1, 500)] public int MaximoParadas { get; init; }
    public bool RequiereFotoIntento { get; init; }
    public bool BloquearDiscrepanciasCarga { get; init; }
    public ConfiguracionRepartoDatos Datos() => new(Version, ZonaHoraria, MaximoParadas,
        RequiereFotoIntento, BloquearDiscrepanciasCarga);
    public static ConfiguracionRepartoContract Desde(ConfiguracionRepartoDatos x) => new()
    {
        Version = x.Version, ZonaHoraria = x.ZonaHoraria, MaximoParadas = x.MaximoParadas,
        RequiereFotoIntento = x.RequiereFotoIntento, BloquearDiscrepanciasCarga = x.BloquearDiscrepanciasCarga
    };
}
