namespace NexoRuta.Domain.ReglasOperativas;

public sealed class VersionReglasOperativas
{
    private readonly List<RequisitoPruebaEntrega> requisitosPrueba = [];
    private readonly List<MotivoNoEntregaVersion> motivos = [];

    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public EstadoVersionReglasOperativas Estado { get; private set; }
    public int? NumeroVersionPublicada { get; private set; }
    public long VersionConcurrencia { get; private set; }
    public int? MaxIntentosEntrega { get; private set; }
    public int? IntervaloEntreIntentosMinutos { get; private set; }
    public PoliticaDevolucionAlAgotarIntentos? PoliticaDevolucionAlAgotarIntentos { get; private set; }
    public int? PlazoEstandarMinutos { get; private set; }
    public int? PlazoUrgenteMinutos { get; private set; }
    public DateTimeOffset CreadaUtc { get; private set; }
    public Guid CreadaPorUsuarioId { get; private set; }
    public DateTimeOffset? PublicadaUtc { get; private set; }
    public Guid? PublicadaPorUsuarioId { get; private set; }
    public IReadOnlyCollection<RequisitoPruebaEntrega> RequisitosPrueba => requisitosPrueba;
    public IReadOnlyCollection<MotivoNoEntregaVersion> Motivos => motivos;
    public IReadOnlyList<string> CamposIncompletos
    {
        get
        {
            var fields = new List<string>();
            if (MaxIntentosEntrega is not (> 0 and <= ReglasOperativas.MaxDeliveryAttempts))
                fields.Add(nameof(MaxIntentosEntrega));
            if (IntervaloEntreIntentosMinutos is not (> 0 and <= ReglasOperativas.MaxDurationMinutes))
                fields.Add(nameof(IntervaloEntreIntentosMinutos));
            if (PoliticaDevolucionAlAgotarIntentos is not { } policy || !Enum.IsDefined(policy))
                fields.Add(nameof(PoliticaDevolucionAlAgotarIntentos));
            if (PlazoEstandarMinutos is not (> 0 and <= ReglasOperativas.MaxDurationMinutes))
                fields.Add(nameof(PlazoEstandarMinutos));
            if (PlazoUrgenteMinutos is not (> 0 and <= ReglasOperativas.MaxDurationMinutes))
                fields.Add(nameof(PlazoUrgenteMinutos));
            if (requisitosPrueba.Count == 0) fields.Add(nameof(RequisitosPrueba));
            if (!motivos.Any(reason => reason.Disponible)) fields.Add(nameof(Motivos));
            return fields;
        }
    }
    public bool EstaCompleta => CamposIncompletos.Count == 0;

    private VersionReglasOperativas() { }

    public static VersionReglasOperativas CrearBorrador(Guid operadorId, Guid usuarioId,
        VersionReglasOperativas? copiarDesde = null, DateTimeOffset? ahoraUtc = null, Guid? id = null)
    {
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (usuarioId == Guid.Empty) throw new ArgumentException("El usuario creador es obligatorio.", nameof(usuarioId));
        if (id == Guid.Empty) throw new ArgumentException("El identificador no puede estar vacío.", nameof(id));
        if (copiarDesde is not null && (copiarDesde.OperadorId != operadorId
            || copiarDesde.Estado != EstadoVersionReglasOperativas.Publicada
            || copiarDesde.NumeroVersionPublicada is null))
            throw new ArgumentException("Solo se puede copiar una publicación del mismo operador.", nameof(copiarDesde));

        var version = new VersionReglasOperativas
        {
            Id = id ?? Guid.CreateVersion7(),
            OperadorId = operadorId,
            Estado = EstadoVersionReglasOperativas.Borrador,
            NumeroVersionPublicada = null,
            VersionConcurrencia = 0,
            CreadaUtc = ValidarUtc(ahoraUtc),
            CreadaPorUsuarioId = usuarioId,
            MaxIntentosEntrega = copiarDesde?.MaxIntentosEntrega,
            IntervaloEntreIntentosMinutos = copiarDesde?.IntervaloEntreIntentosMinutos,
            PoliticaDevolucionAlAgotarIntentos = copiarDesde?.PoliticaDevolucionAlAgotarIntentos,
            PlazoEstandarMinutos = copiarDesde?.PlazoEstandarMinutos,
            PlazoUrgenteMinutos = copiarDesde?.PlazoUrgenteMinutos
        };
        if (copiarDesde is not null)
        {
            version.requisitosPrueba.AddRange(copiarDesde.requisitosPrueba.Select(item => item.Copiar(version.Id)));
            version.motivos.AddRange(copiarDesde.motivos.Select(item => item.Copiar(version.Id)));
        }
        return version;
    }

    public void ReemplazarConfiguracion(long versionEsperada, ReglasOperativasConfiguracion configuracion)
    {
        VerificarBorrador(versionEsperada);
        ArgumentNullException.ThrowIfNull(configuracion);
        ValidarOpcionalPositivo(configuracion.MaxIntentosEntrega, ReglasOperativas.MaxDeliveryAttempts,
            nameof(configuracion.MaxIntentosEntrega));
        ValidarOpcionalPositivo(configuracion.IntervaloEntreIntentosMinutos, ReglasOperativas.MaxDurationMinutes,
            nameof(configuracion.IntervaloEntreIntentosMinutos));
        ValidarOpcionalPositivo(configuracion.PlazoEstandarMinutos, ReglasOperativas.MaxDurationMinutes,
            nameof(configuracion.PlazoEstandarMinutos));
        ValidarOpcionalPositivo(configuracion.PlazoUrgenteMinutos, ReglasOperativas.MaxDurationMinutes,
            nameof(configuracion.PlazoUrgenteMinutos));
        if (configuracion.PoliticaDevolucionAlAgotarIntentos is { } policy && !Enum.IsDefined(policy))
            throw new ArgumentException("La política de devolución no es válida.", nameof(configuracion.PoliticaDevolucionAlAgotarIntentos));

        var requirements = configuracion.RequisitosPrueba
            ?? throw new ArgumentException("Los requisitos de prueba son obligatorios.", nameof(configuracion.RequisitosPrueba));
        if (requirements.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("La configuración incluye un tipo de prueba desconocido.", nameof(configuracion.RequisitosPrueba));
        if (requirements.Distinct().Count() != requirements.Count)
            throw new ArgumentException("No se puede repetir un requisito de prueba.", nameof(configuracion.RequisitosPrueba));

        var motiveInputs = configuracion.Motivos
            ?? throw new ArgumentException("El catálogo de motivos es obligatorio.", nameof(configuracion.Motivos));
        var newMotives = motiveInputs.Select(item => MotivoNoEntregaVersion.Crear(OperadorId, Id, item)).ToList();
        if (newMotives.Select(item => item.Codigo).Distinct(StringComparer.Ordinal).Count() != newMotives.Count)
            throw new ArgumentException("No se puede repetir el código de un motivo.", nameof(configuracion.Motivos));
        var newCodes = newMotives.Select(item => item.Codigo).ToHashSet(StringComparer.Ordinal);
        if (motivos.Any(item => !newCodes.Contains(item.Codigo)))
            throw new ArgumentException("Los códigos existentes no se eliminan; retire su disponibilidad.", nameof(configuracion.Motivos));

        var retainedRequirements = requirements.ToHashSet();
        var newRequirements = requirements.Where(type => requisitosPrueba.All(item => item.Tipo != type))
            .Select(type => RequisitoPruebaEntrega.Crear(OperadorId, Id, type)).ToList();
        var currentMotives = motivos.ToDictionary(item => item.Codigo, StringComparer.Ordinal);
        var addedMotives = newMotives.Where(item => !currentMotives.ContainsKey(item.Codigo)).ToList();
        var nextVersion = checked(VersionConcurrencia + 1);
        MaxIntentosEntrega = configuracion.MaxIntentosEntrega;
        IntervaloEntreIntentosMinutos = configuracion.IntervaloEntreIntentosMinutos;
        PoliticaDevolucionAlAgotarIntentos = configuracion.PoliticaDevolucionAlAgotarIntentos;
        PlazoEstandarMinutos = configuracion.PlazoEstandarMinutos;
        PlazoUrgenteMinutos = configuracion.PlazoUrgenteMinutos;
        requisitosPrueba.RemoveAll(item => !retainedRequirements.Contains(item.Tipo));
        requisitosPrueba.AddRange(newRequirements);
        foreach (var updated in newMotives)
            if (currentMotives.TryGetValue(updated.Codigo, out var existing))
                existing.ActualizarDescripcionYDisponibilidad(updated.Descripcion, updated.Disponible);
        motivos.AddRange(addedMotives);
        VersionConcurrencia = nextVersion;
    }

    public void Publicar(long versionEsperada, int numeroPublicacion, Guid usuarioId,
        DateTimeOffset? ahoraUtc = null)
    {
        VerificarBorrador(versionEsperada);
        if (!EstaCompleta) throw new ReglasOperativasIncompletasException(CamposIncompletos);
        if (numeroPublicacion <= 0) throw new ArgumentOutOfRangeException(nameof(numeroPublicacion));
        if (usuarioId == Guid.Empty) throw new ArgumentException("El usuario publicador es obligatorio.", nameof(usuarioId));
        Estado = EstadoVersionReglasOperativas.Publicada;
        NumeroVersionPublicada = numeroPublicacion;
        PublicadaUtc = ValidarUtc(ahoraUtc);
        PublicadaPorUsuarioId = usuarioId;
    }

    public void VerificarBorrador(long versionEsperada)
    {
        if (Estado != EstadoVersionReglasOperativas.Borrador)
            throw new ConflictoReglasOperativasException(ReglasOperativas.Errors.DraftConflict,
                "La versión ya fue publicada y no se puede editar.");
        if (versionEsperada != VersionConcurrencia)
            throw new ConflictoReglasOperativasException(ReglasOperativas.Errors.DraftConflict,
                "El borrador cambió desde que fue consultado.");
    }

    private static void ValidarOpcionalPositivo(int? value, int max, string parameterName)
    {
        if (value is < 1 || value > max)
            throw new ArgumentOutOfRangeException(parameterName, $"El valor debe estar entre 1 y {max}.");
    }

    private static DateTimeOffset ValidarUtc(DateTimeOffset? value)
    {
        var now = value ?? DateTimeOffset.UtcNow;
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("La fecha debe estar en UTC.", nameof(value));
        return now;
    }
}
