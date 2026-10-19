using NetTopologySuite.Geometries;

namespace NexoRuta.Domain.ZonasCobertura;

public sealed class ZonaCobertura
{
    public const string ZonaHorariaOperativa = "America/Montevideo";

    private readonly List<FranjaEntrega> franjas = [];

    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public string Nombre { get; private set; } = "";
    public EstadoZonaCobertura Estado { get; private set; }
    public MultiPolygon Geometria { get; private set; } = null!;
    public string ZonaHoraria { get; private set; } = ZonaHorariaOperativa;
    public long Version { get; private set; }
    public int RevisionActual { get; private set; }
    public OrigenGeometriaZona OrigenGeometria { get; private set; }
    public string? CodigoDepartamento { get; private set; }
    public string? VersionFuente { get; private set; }
    public DateTimeOffset CreadaUtc { get; private set; }
    public DateTimeOffset ActualizadaUtc { get; private set; }
    public IReadOnlyCollection<FranjaEntrega> Franjas => franjas;

    private ZonaCobertura() { }

    public static ZonaCobertura Crear(
        Guid operadorId,
        string nombre,
        Geometry geometria,
        OrigenGeometriaZona origenGeometria = OrigenGeometriaZona.Dibujada,
        string? codigoDepartamento = null,
        string? versionFuente = null,
        DateTimeOffset? ahoraUtc = null,
        Guid? id = null,
        IEnumerable<FranjaEntregaInicial>? franjasIniciales = null)
    {
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        ValidarNombre(nombre);
        if (!Enum.IsDefined(origenGeometria)) throw new ArgumentOutOfRangeException(nameof(origenGeometria));
        if (id == Guid.Empty) throw new ArgumentException("El identificador de la zona no puede estar vacío.", nameof(id));

        var originCode = string.IsNullOrWhiteSpace(codigoDepartamento) ? null : codigoDepartamento.Trim();
        var sourceVersion = string.IsNullOrWhiteSpace(versionFuente) ? null : versionFuente.Trim();
        if (origenGeometria == OrigenGeometriaZona.Departamento && (originCode is null || sourceVersion is null))
            throw new ArgumentException("Una zona copiada de un departamento requiere código y versión de referencia.");
        if (origenGeometria == OrigenGeometriaZona.Dibujada && (originCode is not null || sourceVersion is not null))
            throw new ArgumentException("Una zona dibujada no puede declarar una referencia departamental.");

        var now = ahoraUtc ?? DateTimeOffset.UtcNow;
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("La fecha debe estar en UTC.", nameof(ahoraUtc));

        var zona = new ZonaCobertura
        {
            Id = id ?? Guid.CreateVersion7(),
            OperadorId = operadorId,
            Nombre = nombre.Trim(),
            Estado = EstadoZonaCobertura.Inactiva,
            Geometria = ValidadorGeometriaCobertura.Normalizar(geometria, nameof(geometria)),
            ZonaHoraria = ZonaHorariaOperativa,
            Version = 0,
            RevisionActual = 1,
            OrigenGeometria = origenGeometria,
            CodigoDepartamento = originCode,
            VersionFuente = sourceVersion,
            CreadaUtc = now,
            ActualizadaUtc = now
        };
        if (franjasIniciales is not null)
            foreach (var initialSlot in franjasIniciales)
            {
                ArgumentNullException.ThrowIfNull(initialSlot);
                var slot = new FranjaEntrega(operadorId, zona.Id,
                    initialSlot.DiaSemanaIso, initialSlot.HoraInicio, initialSlot.HoraFin, initialSlot.Estado);
                zona.ValidarSolapamientos(slot);
                zona.franjas.Add(slot);
            }
        return zona;
    }

    public bool CambiarNombre(string nombre, long versionEsperada, DateTimeOffset? ahoraUtc = null)
    {
        VerificarVersion(versionEsperada);
        ValidarNombre(nombre);
        var normalized = nombre.Trim();
        if (Nombre == normalized) return false;
        var now = ValidarFecha(ahoraUtc);
        Nombre = normalized;
        RegistrarCambio(now);
        return true;
    }

    public bool CambiarGeometria(Geometry geometria, long versionEsperada, DateTimeOffset? ahoraUtc = null)
    {
        VerificarVersion(versionEsperada);
        var normalized = ValidadorGeometriaCobertura.Normalizar(geometria, nameof(geometria));
        if (Geometria.EqualsExact(normalized)) return false;
        var now = ValidarFecha(ahoraUtc);
        Geometria = normalized;
        RegistrarCambio(now);
        return true;
    }

    public bool CambiarConfiguracion(string nombre, Geometry geometria, long versionEsperada, DateTimeOffset? ahoraUtc = null)
    {
        VerificarVersion(versionEsperada);
        ValidarNombre(nombre);
        var normalizedName = nombre.Trim();
        var normalizedGeometry = ValidadorGeometriaCobertura.Normalizar(geometria, nameof(geometria));
        if (Nombre == normalizedName && Geometria.EqualsExact(normalizedGeometry)) return false;
        var now = ValidarFecha(ahoraUtc);
        Nombre = normalizedName;
        Geometria = normalizedGeometry;
        RegistrarCambio(now);
        return true;
    }

    public FranjaEntrega AgregarFranja(
        DiaSemanaIso diaSemanaIso,
        TimeOnly horaInicio,
        TimeOnly horaFin,
        long versionEsperada,
        EstadoFranjaEntrega estado = EstadoFranjaEntrega.Activa,
        Guid? id = null,
        DateTimeOffset? ahoraUtc = null)
    {
        VerificarVersion(versionEsperada);
        var slot = new FranjaEntrega(OperadorId, Id, diaSemanaIso, horaInicio, horaFin, estado, id);
        ValidarSolapamientos(slot);
        var now = ValidarFecha(ahoraUtc);
        franjas.Add(slot);
        RegistrarCambio(now);
        return slot;
    }

    public bool CambiarFranja(
        Guid franjaId,
        DiaSemanaIso diaSemanaIso,
        TimeOnly horaInicio,
        TimeOnly horaFin,
        EstadoFranjaEntrega estado,
        long versionEsperada,
        DateTimeOffset? ahoraUtc = null)
    {
        VerificarVersion(versionEsperada);
        var slot = RequerirFranja(franjaId);
        var candidate = new FranjaEntrega(OperadorId, Id, diaSemanaIso, horaInicio, horaFin, estado, slot.Id);
        ValidarSolapamientos(candidate, slot.Id);
        if (Estado == EstadoZonaCobertura.Activa && slot.EsActiva && !candidate.EsActiva
            && franjas.Count(x => x.EsActiva) == 1)
            throw new ConflictoConfiguracionZonaException("No se puede mantener activa una zona sin franjas activas.");
        if (slot.DiaSemanaIso == diaSemanaIso && slot.HoraInicio == horaInicio
            && slot.HoraFin == horaFin && slot.Estado == estado) return false;
        var now = ValidarFecha(ahoraUtc);
        slot.Actualizar(diaSemanaIso, horaInicio, horaFin, estado);
        RegistrarCambio(now);
        return true;
    }

    public bool CambiarEstadoFranja(Guid franjaId, EstadoFranjaEntrega estado, long versionEsperada, DateTimeOffset? ahoraUtc = null)
    {
        var slot = RequerirFranja(franjaId);
        return CambiarFranja(franjaId, slot.DiaSemanaIso, slot.HoraInicio, slot.HoraFin, estado, versionEsperada, ahoraUtc);
    }

    public bool Activar(long versionEsperada, DateTimeOffset? ahoraUtc = null)
    {
        VerificarVersion(versionEsperada);
        if (Estado == EstadoZonaCobertura.Activa) return false;
        if (!TieneFranjaActiva())
            throw new ConflictoConfiguracionZonaException("La zona requiere al menos una franja activa para activarse.");
        var now = ValidarFecha(ahoraUtc);
        Estado = EstadoZonaCobertura.Activa;
        RegistrarCambio(now);
        return true;
    }

    public bool Desactivar(long versionEsperada, DateTimeOffset? ahoraUtc = null)
    {
        VerificarVersion(versionEsperada);
        if (Estado == EstadoZonaCobertura.Inactiva) return false;
        var now = ValidarFecha(ahoraUtc);
        Estado = EstadoZonaCobertura.Inactiva;
        RegistrarCambio(now);
        return true;
    }

    public void VerificarVersion(long versionEsperada)
    {
        if (versionEsperada != Version)
            throw new ConflictoConcurrenciaZonaException(versionEsperada, Version);
    }

    private bool TieneFranjaActiva() => franjas.Any(x => x.EsActiva);

    private FranjaEntrega RequerirFranja(Guid franjaId)
    {
        if (franjaId == Guid.Empty) throw new ArgumentException("El identificador de la franja es obligatorio.", nameof(franjaId));
        return franjas.SingleOrDefault(x => x.Id == franjaId)
            ?? throw new KeyNotFoundException("La franja no pertenece a esta zona.");
    }

    private void ValidarSolapamientos(FranjaEntrega candidata, Guid? ignorarId = null)
    {
        if (!candidata.EsActiva) return;
        if (franjas.Any(x => x.Id != ignorarId && x.EsActiva && candidata.SeSolapa(x)))
            throw new ConflictoConfiguracionZonaException("Las franjas activas de una zona no pueden superponerse.");
    }

    private void RegistrarCambio(DateTimeOffset ahoraUtc)
    {
        var version = checked(Version + 1);
        var revision = checked(RevisionActual + 1);
        Version = version;
        RevisionActual = revision;
        ActualizadaUtc = ahoraUtc;
    }

    private static DateTimeOffset ValidarFecha(DateTimeOffset? ahoraUtc)
    {
        var now = ahoraUtc ?? DateTimeOffset.UtcNow;
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("La fecha debe estar en UTC.", nameof(ahoraUtc));
        return now;
    }

    private static void ValidarNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 120)
            throw new ArgumentException("El nombre es obligatorio y no puede superar los 120 caracteres.", nameof(nombre));
    }
}
