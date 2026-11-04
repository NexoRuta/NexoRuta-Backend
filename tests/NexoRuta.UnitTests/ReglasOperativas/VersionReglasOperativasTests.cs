using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.UnitTests.ReglasOperativas;

public sealed class VersionReglasOperativasTests
{
    private readonly Guid operadorId = Guid.NewGuid();
    private readonly Guid usuarioId = Guid.NewGuid();

    [Fact]
    public void InitialDraftIsIncompleteAndCannotBePublished()
    {
        var draft = VersionReglasOperativas.CrearBorrador(operadorId, usuarioId);

        Assert.Null(draft.MaxIntentosEntrega);
        Assert.Empty(draft.RequisitosPrueba);
        var incomplete = Assert.Throws<ReglasOperativasIncompletasException>(
            () => draft.Publicar(0, 1, usuarioId));
        Assert.Equal(new[]
        {
            nameof(VersionReglasOperativas.MaxIntentosEntrega),
            nameof(VersionReglasOperativas.IntervaloEntreIntentosMinutos),
            nameof(VersionReglasOperativas.PoliticaDevolucionAlAgotarIntentos),
            nameof(VersionReglasOperativas.PlazoEstandarMinutos),
            nameof(VersionReglasOperativas.PlazoUrgenteMinutos),
            nameof(VersionReglasOperativas.PlazoValidacionPruebaMinutos),
            nameof(VersionReglasOperativas.RequisitosPrueba),
            nameof(VersionReglasOperativas.Motivos)
        }, incomplete.CamposIncompletos);
        Assert.Equal(EstadoVersionReglasOperativas.Borrador, draft.Estado);
    }

    [Fact]
    public void ReplacingConfigurationValidatesBoundsEnumsDuplicatesAndMotives()
    {
        var draft = VersionReglasOperativas.CrearBorrador(operadorId, usuarioId);

        Assert.Throws<ArgumentOutOfRangeException>(() => draft.ReemplazarConfiguracion(0,
            Complete(maxAttempts: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => draft.ReemplazarConfiguracion(0,
            Complete(proofDeadline: 0)));
        Assert.Throws<ArgumentException>(() => draft.ReemplazarConfiguracion(0,
            Complete(requirements: [TipoPruebaEntrega.Fotografia, TipoPruebaEntrega.Fotografia])));
        Assert.Throws<ArgumentException>(() => draft.ReemplazarConfiguracion(0,
            Complete(requirements: [(TipoPruebaEntrega)999])));
        Assert.Throws<ArgumentException>(() => draft.ReemplazarConfiguracion(0,
            Complete(motives: [new("DOMICILIO_CERRADO", "Cerrado", true), new("DOMICILIO_CERRADO", "Cerrado", false)])));
        Assert.Throws<ArgumentException>(() => draft.ReemplazarConfiguracion(0,
            Complete(motives: [new("domicilio-cerrado", "Cerrado", true)])));
    }

    [Fact]
    public void CopyPreservesStableMotiveCodesAndPublishedSnapshotIsIndependent()
    {
        var first = VersionReglasOperativas.CrearBorrador(operadorId, usuarioId);
        first.ReemplazarConfiguracion(0, Complete());
        first.Publicar(1, 1, usuarioId);

        var root = ReglasOperativasOperador.Crear(operadorId);
        root.Publicar(first);
        var copied = VersionReglasOperativas.CrearBorrador(operadorId, usuarioId, first);
        Assert.Equal(0, copied.VersionConcurrencia);
        var changed = Complete(motives: [new("DOMICILIO_CERRADO", "No había receptor en el domicilio", true)]);
        copied.ReemplazarConfiguracion(0, changed);

        Assert.Equal("DOMICILIO_CERRADO", Assert.Single(copied.Motivos).Codigo);
        Assert.Equal("Domicilio cerrado", Assert.Single(first.Motivos).Descripcion);
        Assert.Equal("No había receptor en el domicilio", Assert.Single(copied.Motivos).Descripcion);
        Assert.Equal(1, copied.VersionConcurrencia);
        Assert.Equal(1, first.VersionConcurrencia);
        Assert.Equal(90, copied.PlazoValidacionPruebaMinutos);
    }

    [Fact]
    public void PublicationRequiresPositiveCompleteFieldsAndSeparateVersionNumber()
    {
        var draft = VersionReglasOperativas.CrearBorrador(operadorId, usuarioId);
        draft.ReemplazarConfiguracion(0, Complete());
        var root = ReglasOperativasOperador.Crear(operadorId);

        draft.Publicar(1, 1, usuarioId);
        root.Publicar(draft);

        Assert.Equal(1, draft.VersionConcurrencia);
        Assert.Equal(1, draft.NumeroVersionPublicada);
        Assert.Equal(draft.Id, root.VersionVigenteId);
        Assert.Equal(1, root.UltimoNumeroPublicacion);
        Assert.Throws<ConflictoReglasOperativasException>(
            () => draft.ReemplazarConfiguracion(1, Complete()));
    }

    [Fact]
    public void MotiveCodesCannotBeRemovedFromDraftAndStaleVersionDoesNotMutateIt()
    {
        var draft = VersionReglasOperativas.CrearBorrador(operadorId, usuarioId);
        draft.ReemplazarConfiguracion(0, Complete());
        var current = draft.VersionConcurrencia;

        Assert.Throws<ArgumentException>(() => draft.ReemplazarConfiguracion(current,
            Complete(motives: [])));
        Assert.Throws<ConflictoReglasOperativasException>(() => draft.ReemplazarConfiguracion(0,
            Complete(maxAttempts: 4)));

        Assert.Equal(current, draft.VersionConcurrencia);
        Assert.Equal(3, draft.MaxIntentosEntrega);
        Assert.Single(draft.Motivos);
    }

    private static ReglasOperativasConfiguracion Complete(
        int maxAttempts = 3,
        int? proofDeadline = 90,
        IReadOnlyCollection<TipoPruebaEntrega>? requirements = null,
        IReadOnlyCollection<MotivoNoEntregaConfiguracion>? motives = null)
        => new(maxAttempts, 1440, PoliticaDevolucionAlAgotarIntentos.DecisionOperativa,
            4320, 1440, proofDeadline, requirements ?? [TipoPruebaEntrega.FirmaEnPantalla, TipoPruebaEntrega.NombreReceptor],
            motives ?? [new("DOMICILIO_CERRADO", "Domicilio cerrado", true)]);
}
