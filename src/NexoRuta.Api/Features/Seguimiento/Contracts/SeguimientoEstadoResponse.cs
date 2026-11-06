using NexoRuta.Api.Features.IdentidadVisual.Contracts;

namespace NexoRuta.Api.Features.Seguimiento.Contracts;

public sealed record SeguimientoEstadoResponse(
    string Estado,
    IdentidadVisualPublicaResponse Identidad)
{ public NexoRuta.Application.Seguimiento.Dtos.VentanaEstimadaPublica? VentanaEstimada { get; init; } public IReadOnlyList<NexoRuta.Application.Seguimiento.Dtos.EventoPublico> Historial { get; init; } = []; public IReadOnlyList<NexoRuta.Application.Seguimiento.Dtos.BultoPublico> Bultos { get; init; } = []; public int TotalBultos { get; init; } public int BultosEntregados { get; init; } public int BultosDevueltos { get; init; } public bool EsRetiro { get; init; } }
