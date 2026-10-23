using NexoRuta.Api.Features.IdentidadVisual.Contracts;

namespace NexoRuta.Api.Features.Seguimiento.Contracts;

public sealed record SeguimientoEstadoResponse(
    string Estado,
    IdentidadVisualPublicaResponse Identidad);
