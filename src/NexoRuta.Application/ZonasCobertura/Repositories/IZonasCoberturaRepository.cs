using NetTopologySuite.Geometries;
using NexoRuta.Domain.ZonasCobertura;

namespace NexoRuta.Application.ZonasCobertura.Repositories;

public interface IZonasCoberturaRepository
{
    Task<(IReadOnlyList<ZonaCoberturaResumen> Items, int Total)> ListarAsync(
        EstadoZonaCobertura? estado, int pagina, int tamanoPagina, CancellationToken cancellationToken = default);
    Task<ZonaCobertura?> ObtenerAsync(Guid zonaId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ZonaCobertura>> ConsultarCoberturaAsync(double longitud, double latitud, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DepartamentoReferenciaResumen>> ListarDepartamentosAsync(CancellationToken cancellationToken = default);
    Task<DepartamentoReferencia?> ObtenerDepartamentoAsync(string codigo, CancellationToken cancellationToken = default);
    Task<TerritorioUruguayReferencia?> ObtenerTerritorioUruguayAsync(CancellationToken cancellationToken = default);
    Task<DepartamentoReferenciaConGeometria?> ObtenerDepartamentoConGeometriaAsync(string codigo, CancellationToken cancellationToken = default);
    Task<ResultadoValidacionTerritorial> ValidarTerritorioUruguayAsync(Geometry geometria, CancellationToken cancellationToken = default);
    Task GuardarNuevaAsync(ZonaCobertura zona, Guid usuarioId, CancellationToken cancellationToken = default);
    Task GuardarCambioAsync(ZonaCobertura zona, long versionEsperada, Guid usuarioId, CancellationToken cancellationToken = default);
}
