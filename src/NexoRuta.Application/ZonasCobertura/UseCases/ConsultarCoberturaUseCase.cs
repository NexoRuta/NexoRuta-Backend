using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Application.ZonasCobertura.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ZonasCobertura;
using NetTopologySuite;

namespace NexoRuta.Application.ZonasCobertura.UseCases;

public sealed class ConsultarCoberturaUseCase(
    RequerirOperadorCoberturaUseCase acceso,
    IZonasCoberturaRepository zonas,
    ValidarTerritorioUruguayUseCase territorio)
{
    public async Task<IReadOnlyList<ZonaCobertura>> EjecutarAsync(
        double longitud,
        double latitud,
        CancellationToken cancellationToken = default)
    {
        await acceso.EjecutarAsync(cancellationToken);
        if (!double.IsFinite(longitud) || longitud is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitud));
        if (!double.IsFinite(latitud) || latitud is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitud));
        var point = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326)
            .CreatePoint(new NetTopologySuite.Geometries.Coordinate(longitud, latitud));
        await territorio.VerificarAsync(point, cancellationToken);

        return await zonas.ConsultarCoberturaAsync(longitud, latitud, cancellationToken);
    }
}
