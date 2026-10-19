using NetTopologySuite.Geometries;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Application.ZonasCobertura.Repositories;

namespace NexoRuta.Application.ZonasCobertura.UseCases;

public sealed class ValidarTerritorioUruguayUseCase(IZonasCoberturaRepository zonas)
{
    public async Task VerificarAsync(Geometry geometria, CancellationToken cancellationToken = default)
    {
        switch (await zonas.ValidarTerritorioUruguayAsync(geometria, cancellationToken))
        {
            case ResultadoValidacionTerritorial.Cubierta:
                return;
            case ResultadoValidacionTerritorial.FueraDelTerritorio:
                throw new GeometriaFueraDeUruguayException();
            default:
                throw new ReferenciaTerritorialUruguayNoDisponibleException();
        }
    }

    public async Task<TerritorioUruguayReferencia> ObtenerAsync(CancellationToken cancellationToken = default)
        => await zonas.ObtenerTerritorioUruguayAsync(cancellationToken)
            ?? throw new ReferenciaTerritorialUruguayNoDisponibleException();
}
