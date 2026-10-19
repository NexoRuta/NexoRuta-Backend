using NexoRuta.Domain.Tarifas;

namespace NexoRuta.UnitTests.Tarifas;

public sealed class CalculadoraTarifasTests
{
    [Fact]
    public void CalculaPesoVolumenPorBultoYAplicaBaseUnaSolaVez()
    {
        var tarifa = new TarifaCalculo(
            Guid.NewGuid(), ModalidadTarifaria.Estandar, 120m, 25.50m, 900m, []);
        var bultos = new[]
        {
            new BultoCalculo(1500m, 40m, 30m, 20m),
            new BultoCalculo(2000m, 50m, 20m, 10m)
        };

        var resultado = CalculadoraTarifas.Calcular(tarifa, bultos);

        Assert.Equal(3.5m, resultado.PesoTotalKilogramos);
        Assert.Equal(0.034m, resultado.VolumenTotalMetrosCubicos);
        Assert.Equal(120m, resultado.ImporteBaseUyu);
        Assert.Equal(89.25m, resultado.ComponentePesoUyu);
        Assert.Equal(30.6m, resultado.ComponenteVolumenUyu);
        Assert.Equal(239.85m, resultado.SubtotalUyu);
        Assert.Equal(239.85m, resultado.TotalExactoUyu);
        Assert.Equal(239.85m, resultado.TotalFinalUyu);
        Assert.Equal(MonedaTarifaria.UYU, resultado.Moneda);
    }

    [Fact]
    public void DiferenciaTarifaConfiguradaEnCeroDeFilaAusenteYCalculaAjustesSobreSubtotal()
    {
        var tarifa = new TarifaCalculo(Guid.NewGuid(), ModalidadTarifaria.Urgente,
            0m, 0m, 0m,
            [
                new AjusteCalculo("Fijo", TipoAjusteTarifario.RecargoFijo, 12m),
                new AjusteCalculo("Porcentaje", TipoAjusteTarifario.RecargoPorcentual, 15m),
                new AjusteCalculo("Bonificación fija", TipoAjusteTarifario.BonificacionFija, 10m),
                new AjusteCalculo("Bonificación porcentual", TipoAjusteTarifario.BonificacionPorcentual, 5m)
            ]);
        var rateWithBase = tarifa with { ImporteBaseUyu = 239.85m };

        var resultado = CalculadoraTarifas.Calcular(rateWithBase, [new(3500m, 1m, 1m, 1m)]);

        Assert.Equal(239.85m, resultado.SubtotalUyu);
        Assert.Equal(47.9775m, resultado.TotalRecargosUyu);
        Assert.Equal(21.9925m, resultado.TotalBonificacionesUyu);
        Assert.Equal(265.835m, resultado.TotalExactoUyu);
        Assert.Equal(265.84m, resultado.TotalFinalUyu);
        Assert.Collection(resultado.Ajustes,
            adjustment => Assert.Equal(TipoAjusteTarifario.RecargoFijo, adjustment.Tipo),
            adjustment => Assert.Equal(TipoAjusteTarifario.RecargoPorcentual, adjustment.Tipo),
            adjustment => Assert.Equal(TipoAjusteTarifario.BonificacionFija, adjustment.Tipo),
            adjustment => Assert.Equal(TipoAjusteTarifario.BonificacionPorcentual, adjustment.Tipo));
        Assert.Equal(0m, CalculadoraTarifas.Calcular(tarifa with { Ajustes = [] }, [new(1m, 1m, 1m, 1m)]).TotalFinalUyu);
    }

    [Fact]
    public void RedondeaMitadAlejadaDeCeroYAplicaPisoCeroConDesglose()
    {
        var halfCent = new TarifaCalculo(Guid.NewGuid(), ModalidadTarifaria.Estandar,
            1m, 0.005m, 0m, []);
        var negative = halfCent with
        {
            ImporteBaseUyu = 1m,
            Ajustes = [new("Exceso", TipoAjusteTarifario.BonificacionFija, 5m)]
        };

        var rounded = CalculadoraTarifas.Calcular(halfCent, [new(1000m, 1m, 1m, 1m)]);
        var floored = CalculadoraTarifas.Calcular(negative, [new(1m, 1m, 1m, 1m)]);

        Assert.Equal(1.01m, rounded.TotalFinalUyu);
        Assert.Equal(-3.999995m, floored.TotalExactoUyu);
        Assert.Equal(0m, floored.TotalFinalUyu);
        Assert.True(floored.PisoCeroAplicado);
    }

    [Fact]
    public void RechazaEntradasInvalidasYDesbordamiento()
    {
        var tarifa = new TarifaCalculo(Guid.NewGuid(), ModalidadTarifaria.Estandar,
            0m, 0m, 0m, []);

        Assert.Throws<ArgumentException>(() => CalculadoraTarifas.Calcular(tarifa, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => CalculadoraTarifas.Calcular(tarifa, [new(0m, 1m, 1m, 1m)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => CalculadoraTarifas.Calcular(
            tarifa with { ImporteBaseUyu = -1m }, [new(1m, 1m, 1m, 1m)]));
        Assert.Throws<OverflowException>(() => CalculadoraTarifas.Calcular(
            tarifa, [new(1m, ReglasTarifario.MaxBultoMeasure, ReglasTarifario.MaxBultoMeasure,
                ReglasTarifario.MaxBultoMeasure)]));
    }

    [Fact]
    public void AjustePorcentualFueraDeRangoSeRechaza()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AjusteCalculo(
            "Exceso", TipoAjusteTarifario.RecargoPorcentual, 100.01m).Validar());
    }

    [Fact]
    public void BorradorVersionaConfiguracionPublicaDeFormaInmutableYCopiaInclusoZonasInactivas()
    {
        var operatorId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var draft = VersionTarifario.CrearBorrador(operatorId, userId);
        draft.ReemplazarConfiguracion(0,
        [
            new(zoneId, ModalidadTarifaria.Estandar, 100m, 1m, 0m,
                [new("Prueba", TipoAjusteTarifario.RecargoFijo, 5m)])
        ]);

        Assert.Equal(1, draft.VersionConcurrencia);
        Assert.Throws<ConflictoTarifarioException>(() => draft.ReemplazarConfiguracion(0, []));
        Assert.Throws<ConflictoTarifarioException>(() => draft.Publicar(1, 1, userId, new HashSet<Guid>()));
        Assert.Equal(EstadoVersionTarifario.Borrador, draft.Estado);
        Assert.Equal(1, draft.VersionConcurrencia);

        draft.Publicar(1, 1, userId, new HashSet<Guid> { zoneId });
        var tariffBook = TarifarioOperador.Crear(operatorId);
        tariffBook.Publicar(draft);
        Assert.Equal(1, tariffBook.UltimoNumeroPublicacion);
        Assert.Equal(draft.Id, tariffBook.VersionVigenteId);
        Assert.Throws<ConflictoTarifarioException>(() => draft.ReemplazarConfiguracion(1, []));

        var nextDraft = VersionTarifario.CrearBorrador(operatorId, userId, draft.Tarifas);
        var copiedRate = Assert.Single(nextDraft.Tarifas);
        Assert.NotEqual(draft.Tarifas.Single().VersionId, copiedRate.VersionId);
        Assert.Equal(draft.Tarifas.Single().ZonaId, copiedRate.ZonaId);
        Assert.Equal(draft.Tarifas.Single().Ajustes.Single().Tipo, copiedRate.Ajustes.Single().Tipo);
    }
}
