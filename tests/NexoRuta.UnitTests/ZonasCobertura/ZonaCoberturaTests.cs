using NetTopologySuite;
using NetTopologySuite.Geometries;
using NexoRuta.Domain.ZonasCobertura;

namespace NexoRuta.UnitTests.ZonasCobertura;

public sealed class ZonaCoberturaTests
{
    private static readonly Guid OperadorId = Guid.Parse("0c9b7698-0311-4a34-ae1c-77466fbcd008");
    private static readonly GeometryFactory GeometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    [Fact]
    public void NuevaZonaComienzaInactivaYNoSeActivaSinFranjaActiva()
    {
        var zona = CrearZona();

        Assert.Equal(EstadoZonaCobertura.Inactiva, zona.Estado);
        Assert.Equal(0, zona.Version);
        Assert.Equal(1, zona.RevisionActual);
        Assert.Throws<ConflictoConfiguracionZonaException>(() => zona.Activar(zona.Version));
    }

    [Fact]
    public void FranjasActivasNoPuedenSolaparseDuranteLaNoche()
    {
        var zona = CrearZona();
        zona.AgregarFranja(DiaSemanaIso.Lunes, new TimeOnly(22, 0), new TimeOnly(2, 0), zona.Version);

        Assert.Throws<ConflictoConfiguracionZonaException>(() => zona.AgregarFranja(
            DiaSemanaIso.Martes, new TimeOnly(1, 0), new TimeOnly(3, 0), zona.Version));
    }

    [Fact]
    public void SolapamientoSemanalConsideraCruceDomingoALunes()
    {
        var zona = CrearZona();
        zona.AgregarFranja(DiaSemanaIso.Domingo, new TimeOnly(23, 0), new TimeOnly(1, 0), zona.Version);

        Assert.Throws<ConflictoConfiguracionZonaException>(() => zona.AgregarFranja(
            DiaSemanaIso.Lunes, new TimeOnly(0, 30), new TimeOnly(2, 0), zona.Version));
    }

    [Fact]
    public void IntervalosContiguosSeAceptanYLaGeometriaPolygonSeNormaliza()
    {
        var zona = CrearZona();
        zona.AgregarFranja(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(12, 0), zona.Version);
        zona.AgregarFranja(DiaSemanaIso.Lunes, new TimeOnly(12, 0), new TimeOnly(15, 0), zona.Version);

        Assert.IsType<MultiPolygon>(zona.Geometria);
        Assert.Equal(4326, zona.Geometria.SRID);
        Assert.Equal(2, zona.Franjas.Count);
    }

    [Fact]
    public void FranjasConSegundosSolapadosNoSeConsideranContiguas()
    {
        var zona = CrearZona();
        zona.AgregarFranja(DiaSemanaIso.Lunes, new TimeOnly(9, 0, 30), new TimeOnly(10, 0, 30), zona.Version);

        Assert.Throws<ConflictoConfiguracionZonaException>(() => zona.AgregarFranja(
            DiaSemanaIso.Lunes, new TimeOnly(10, 0), new TimeOnly(11, 0), zona.Version));
    }

    [Fact]
    public void FranjaRechazaPrecisionMasFinaQuePostgreSqlTime()
    {
        var zona = CrearZona();
        var horaSubMicrosegundo = new TimeOnly(9, 0).Add(TimeSpan.FromTicks(1));

        Assert.Throws<ArgumentException>(() => zona.AgregarFranja(
            DiaSemanaIso.Lunes, horaSubMicrosegundo, new TimeOnly(10, 0), zona.Version));
    }

    [Fact]
    public void FranjasInicialesFormanLaRevisionUnoSinIncrementarConcurrencia()
    {
        var zona = ZonaCobertura.Crear(OperadorId, "Centro", Geometria(), franjasIniciales:
        [
            new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0)),
            new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0), EstadoFranjaEntrega.Inactiva)
        ]);

        Assert.Equal(0, zona.Version);
        Assert.Equal(1, zona.RevisionActual);
        Assert.Equal(2, zona.Franjas.Count);
        Assert.True(zona.Activar(zona.Version));
    }

    [Fact]
    public void ZonaActivaNoPuedePerderSuUltimaFranjaActiva()
    {
        var zona = ZonaCobertura.Crear(OperadorId, "Centro", Geometria(), franjasIniciales:
        [new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0))]);
        zona.Activar(zona.Version);

        Assert.Throws<ConflictoConfiguracionZonaException>(() => zona.CambiarEstadoFranja(
            zona.Franjas.Single().Id, EstadoFranjaEntrega.Inactiva, zona.Version));
        Assert.Equal(EstadoZonaCobertura.Activa, zona.Estado);
        Assert.Equal(EstadoFranjaEntrega.Activa, zona.Franjas.Single().Estado);
    }

    [Fact]
    public void EdicionConVersionObsoletaSeRechazaAntesDeCambiarElAgregado()
    {
        var zona = CrearZona();
        zona.AgregarFranja(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0), zona.Version);

        Assert.Throws<ConflictoConcurrenciaZonaException>(() => zona.Activar(0));
        Assert.Equal(EstadoZonaCobertura.Inactiva, zona.Estado);
        Assert.Equal(1, zona.Version);
    }

    [Fact]
    public void ActualizacionDeNombreYGeometriaIncrementaVersionYRevisionUnaSolaVez()
    {
        var zone = CrearZona();
        var changedGeometry = GeometryFactory.CreatePolygon(GeometryFactory.CreateLinearRing(
            [new(-56, -34), new(-54, -34), new(-54, -33), new(-56, -34)]));

        Assert.True(zone.CambiarConfiguracion("Nuevo nombre", changedGeometry, 0));
        Assert.Equal("Nuevo nombre", zone.Nombre);
        Assert.Equal(1, zone.Version);
        Assert.Equal(2, zone.RevisionActual);
        Assert.True(zone.Geometria.Covers(GeometryFactory.CreatePoint(new Coordinate(-55, -33.5))));
        Assert.False(zone.CambiarConfiguracion("Nuevo nombre", changedGeometry, 1));
        Assert.Equal(1, zone.Version);
        Assert.Equal(2, zone.RevisionActual);
    }

    [Fact]
    public void GeometriaInvalidaFueraDeWgs84OSinSridSeRechaza()
    {
        var selfIntersecting = GeometryFactory.CreatePolygon(GeometryFactory.CreateLinearRing(
        [
            new Coordinate(0, 0), new Coordinate(2, 2), new Coordinate(0, 2),
            new Coordinate(2, 0), new Coordinate(0, 0)
        ]));
        var outOfRange = GeometryFactory.CreatePolygon(GeometryFactory.CreateLinearRing(
        [
            new Coordinate(181, 0), new Coordinate(182, 0), new Coordinate(182, 1),
            new Coordinate(181, 0)
        ]));
        var threeDimensional = GeometryFactory.CreatePolygon(GeometryFactory.CreateLinearRing(
        [
            new CoordinateZ(0, 0, 5), new CoordinateZ(1, 0, 5),
            new CoordinateZ(1, 1, 5), new CoordinateZ(0, 0, 5)
        ]));
        var noSrid = new GeometryFactory().CreatePolygon(new GeometryFactory().CreateLinearRing(
        [
            new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1),
            new Coordinate(0, 0)
        ]));
        var empty = GeometryFactory.CreatePolygon();

        Assert.Throws<ArgumentException>(() => ZonaCobertura.Crear(OperadorId, "Autointersección", selfIntersecting));
        Assert.Throws<ArgumentException>(() => ZonaCobertura.Crear(OperadorId, "Fuera de rango", outOfRange));
        Assert.Throws<ArgumentException>(() => ZonaCobertura.Crear(OperadorId, "Tres dimensiones", threeDimensional));
        Assert.Throws<ArgumentException>(() => ZonaCobertura.Crear(OperadorId, "SRID faltante", noSrid));
        Assert.Throws<ArgumentException>(() => ZonaCobertura.Crear(OperadorId, "Vacía", empty));
    }

    private static ZonaCobertura CrearZona() => ZonaCobertura.Crear(OperadorId, "Centro", Geometria());

    private static Polygon Geometria() => GeometryFactory.CreatePolygon(GeometryFactory.CreateLinearRing(
    [
        new Coordinate(-56, -34), new Coordinate(-55, -34),
        new Coordinate(-55, -33), new Coordinate(-56, -33), new Coordinate(-56, -34)
    ]));
}
