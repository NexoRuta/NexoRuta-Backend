using System.ComponentModel.DataAnnotations;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record ConfirmarDestinoRequest([Range(0, long.MaxValue)] long Version,
    [Range(-90, 90)] double Latitud, [Range(-180, 180)] double Longitud);
