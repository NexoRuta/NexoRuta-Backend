using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed class ReprogramarEnvioRequest
{
    [Range(0, long.MaxValue)] public long VersionEnvio { get; init; }
}
