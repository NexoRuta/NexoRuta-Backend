using System.ComponentModel.DataAnnotations;
using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed class ConfiguracionRepartoContract
{
    [Range(0, long.MaxValue)] public long Version { get; init; }
    [Required, StringLength(100)] public string ZonaHoraria { get; init; } = "";
    [Range(1, 500)] public int MaximoParadas { get; init; }
    [Range(1, 100)] public int MaximoIntentos { get; init; }
    public bool RequiereFirma { get; init; }
    public bool RequiereFoto { get; init; }
    public bool RequiereNombre { get; init; }
    public bool RequiereDocumento { get; init; }
    public bool RequiereFotoIntento { get; init; }
    public bool BloquearDiscrepanciasCarga { get; init; }
    [Required, MinLength(1), MaxLength(100)] public string[] Motivos { get; init; } = [];
    public ConfiguracionRepartoDatos Datos() => new(Version, ZonaHoraria, MaximoParadas, MaximoIntentos,
        RequiereFirma, RequiereFoto, RequiereNombre, RequiereDocumento, RequiereFotoIntento, BloquearDiscrepanciasCarga, Motivos);
    public static ConfiguracionRepartoContract Desde(ConfiguracionRepartoDatos x) => new()
    {
        Version = x.Version, ZonaHoraria = x.ZonaHoraria, MaximoParadas = x.MaximoParadas, MaximoIntentos = x.MaximoIntentos,
        RequiereFirma = x.RequiereFirma, RequiereFoto = x.RequiereFoto, RequiereNombre = x.RequiereNombre,
        RequiereDocumento = x.RequiereDocumento, RequiereFotoIntento = x.RequiereFotoIntento,
        BloquearDiscrepanciasCarga = x.BloquearDiscrepanciasCarga, Motivos = x.Motivos
    };
}
