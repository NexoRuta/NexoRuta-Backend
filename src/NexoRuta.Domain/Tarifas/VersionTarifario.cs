namespace NexoRuta.Domain.Tarifas;

public sealed class VersionTarifario
{
    private readonly List<TarifaZonaModalidad> tarifas = [];

    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public EstadoVersionTarifario Estado { get; private set; }
    public int? NumeroVersionPublicada { get; private set; }
    public long VersionConcurrencia { get; private set; }
    public MonedaTarifaria Moneda { get; private set; }
    public DateTimeOffset CreadaUtc { get; private set; }
    public Guid CreadaPorUsuarioId { get; private set; }
    public DateTimeOffset? PublicadaUtc { get; private set; }
    public Guid? PublicadaPorUsuarioId { get; private set; }
    public IReadOnlyCollection<TarifaZonaModalidad> Tarifas => tarifas;

    private VersionTarifario() { }

    public static VersionTarifario CrearBorrador(
        Guid operadorId, Guid usuarioId, IEnumerable<TarifaZonaModalidad>? tarifasCopiadas = null,
        DateTimeOffset? ahoraUtc = null, Guid? id = null)
    {
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (usuarioId == Guid.Empty) throw new ArgumentException("El usuario creador es obligatorio.", nameof(usuarioId));
        if (id == Guid.Empty) throw new ArgumentException("El identificador no puede estar vacío.", nameof(id));
        var now = ValidarUtc(ahoraUtc);
        var version = new VersionTarifario
        {
            Id = id ?? Guid.CreateVersion7(),
            OperadorId = operadorId,
            Estado = EstadoVersionTarifario.Borrador,
            NumeroVersionPublicada = null,
            VersionConcurrencia = 0,
            Moneda = MonedaTarifaria.UYU,
            CreadaUtc = now,
            CreadaPorUsuarioId = usuarioId
        };
        if (tarifasCopiadas is not null)
            foreach (var source in tarifasCopiadas)
            {
                ArgumentNullException.ThrowIfNull(source);
                if (source.OperadorId != operadorId) throw new ArgumentException("La tarifa copiada pertenece a otro operador.", nameof(tarifasCopiadas));
                version.tarifas.Add(source.Copiar(version.Id));
            }
        return version;
    }

    public void ReemplazarTarifas(long versionEsperada, IEnumerable<TarifaZonaModalidad> nuevasTarifas)
    {
        VerificarBorrador(versionEsperada);
        ArgumentNullException.ThrowIfNull(nuevasTarifas);
        var copy = nuevasTarifas.ToList();
        if (copy.Any(x => x is null)) throw new ArgumentException("La configuración incluye una tarifa nula.", nameof(nuevasTarifas));
        if (copy.Any(x => x.OperadorId != OperadorId || x.VersionId != Id))
            throw new ArgumentException("Todas las tarifas deben pertenecer al operador y borrador actuales.", nameof(nuevasTarifas));
        if (copy.GroupBy(x => (x.ZonaId, x.Modalidad)).Any(group => group.Count() > 1))
            throw new ArgumentException("No se puede repetir una combinación de zona y modalidad.", nameof(nuevasTarifas));
        var nextVersion = checked(VersionConcurrencia + 1);
        tarifas.Clear();
        tarifas.AddRange(copy);
        VersionConcurrencia = nextVersion;
    }

    public void ReemplazarConfiguracion(long versionEsperada,
        IEnumerable<TarifaZonaModalidadConfiguracion> configuracion)
    {
        VerificarBorrador(versionEsperada);
        ArgumentNullException.ThrowIfNull(configuracion);
        var replacement = configuracion.Select(row =>
        {
            ArgumentNullException.ThrowIfNull(row);
            return TarifaZonaModalidad.Crear(OperadorId, Id, row.ZonaId, row.Modalidad,
                row.ImporteBaseUyu, row.PrecioPorKilogramoUyu, row.PrecioPorMetroCubicoUyu,
                row.Ajustes?.Select(adjustment =>
                {
                    ArgumentNullException.ThrowIfNull(adjustment);
                    return AjusteTarifario.Crear(OperadorId, Id, row.ZonaId, row.Modalidad,
                        adjustment.Nombre, adjustment.Tipo, adjustment.ImporteFijoUyu,
                        adjustment.Porcentaje);
                }));
        }).ToList();
        ReemplazarTarifas(versionEsperada, replacement);
    }

    public void Publicar(long versionEsperada, int numeroPublicacion, Guid usuarioId,
        IReadOnlySet<Guid> zonasActivas, DateTimeOffset? ahoraUtc = null)
    {
        VerificarBorrador(versionEsperada);
        if (numeroPublicacion <= 0) throw new ArgumentOutOfRangeException(nameof(numeroPublicacion));
        if (usuarioId == Guid.Empty) throw new ArgumentException("El usuario publicador es obligatorio.", nameof(usuarioId));
        ArgumentNullException.ThrowIfNull(zonasActivas);
        if (!tarifas.Any(rate => zonasActivas.Contains(rate.ZonaId)))
            throw new ConflictoTarifarioException(ReglasTarifario.Errors.NoActiveOffer,
                "Se requiere al menos una tarifa válida para una zona activa.");
        var now = ValidarUtc(ahoraUtc);
        Estado = EstadoVersionTarifario.Publicada;
        NumeroVersionPublicada = numeroPublicacion;
        PublicadaUtc = now;
        PublicadaPorUsuarioId = usuarioId;
    }

    public void VerificarBorrador(long versionEsperada)
    {
        if (Estado != EstadoVersionTarifario.Borrador)
            throw new ConflictoTarifarioException(ReglasTarifario.Errors.DraftConflict,
                "La versión ya fue publicada y no se puede editar.");
        if (versionEsperada != VersionConcurrencia)
            throw new ConflictoTarifarioException(ReglasTarifario.Errors.DraftConflict,
                "El borrador cambió desde que fue consultado.");
    }

    public TarifaZonaModalidad? BuscarTarifa(Guid zonaId, ModalidadTarifaria modalidad)
        => tarifas.SingleOrDefault(x => x.ZonaId == zonaId && x.Modalidad == modalidad);

    private static DateTimeOffset ValidarUtc(DateTimeOffset? value)
    {
        var now = value ?? DateTimeOffset.UtcNow;
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("La fecha debe estar en UTC.", nameof(value));
        return now;
    }
}
