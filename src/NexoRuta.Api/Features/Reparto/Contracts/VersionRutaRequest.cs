using System.ComponentModel.DataAnnotations;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record VersionRutaRequest([Range(0, long.MaxValue)] long Version);
