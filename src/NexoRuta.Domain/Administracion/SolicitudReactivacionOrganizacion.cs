namespace NexoRuta.Domain.Administracion;

public sealed class SolicitudReactivacionOrganizacion
{
    public Guid Id { get; private set; }
    public Guid? OperadorId { get; private set; }
    public Guid? ComercioId { get; private set; }
    public TipoAccesoUsuario TipoOrganizacion { get; private set; }
    public Guid OrganizacionId { get; private set; }
    public Guid SolicitadaPorUsuarioId { get; private set; }
    public DateTimeOffset SolicitadaEnUtc { get; private set; }
    public string Motivo { get; private set; } = "";
    public EstadoSolicitudReactivacion Estado { get; private set; } = EstadoSolicitudReactivacion.Pendiente;
    public Guid? ResueltaPorUsuarioId { get; private set; }
    public DateTimeOffset? ResueltaEnUtc { get; private set; }
    public string? MotivoResolucion { get; private set; }
    public long Version { get; private set; }

    private SolicitudReactivacionOrganizacion() { } // EF Core

    public SolicitudReactivacionOrganizacion(
        TipoAccesoUsuario tipoOrganizacion,
        Guid organizacionId,
        Guid solicitadaPorUsuarioId,
        DateTimeOffset solicitadaEnUtc,
        string motivo)
    {
        if (tipoOrganizacion is not (TipoAccesoUsuario.Operador or TipoAccesoUsuario.Comercio))
            throw new ArgumentOutOfRangeException(nameof(tipoOrganizacion));
        if (organizacionId == Guid.Empty || solicitadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("An organization and requesting identity are required.");

        var cleanedReason = ValidarMotivo(motivo, nameof(motivo));
        ValidarUtc(solicitadaEnUtc, nameof(solicitadaEnUtc));

        Id = Guid.CreateVersion7();
        TipoOrganizacion = tipoOrganizacion;
        OrganizacionId = organizacionId;
        OperadorId = tipoOrganizacion == TipoAccesoUsuario.Operador ? organizacionId : null;
        ComercioId = tipoOrganizacion == TipoAccesoUsuario.Comercio ? organizacionId : null;
        SolicitadaPorUsuarioId = solicitadaPorUsuarioId;
        SolicitadaEnUtc = solicitadaEnUtc;
        Motivo = cleanedReason;
    }

    public bool Resolver(
        EstadoSolicitudReactivacion estado,
        Guid resueltaPorUsuarioId,
        DateTimeOffset resueltaEnUtc,
        string motivo)
    {
        if (estado is not (EstadoSolicitudReactivacion.Aprobada or EstadoSolicitudReactivacion.Rechazada or EstadoSolicitudReactivacion.AtendidaPorSoporte))
            throw new ArgumentOutOfRangeException(nameof(estado));
        if (resueltaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("A resolving identity is required.", nameof(resueltaPorUsuarioId));

        var cleanedReason = ValidarMotivo(motivo, nameof(motivo));
        ValidarUtc(resueltaEnUtc, nameof(resueltaEnUtc));

        if (Estado != EstadoSolicitudReactivacion.Pendiente)
        {
            if (Estado == estado && ResueltaPorUsuarioId == resueltaPorUsuarioId && MotivoResolucion == cleanedReason)
                return false;
            throw new InvalidOperationException("The reactivation request has already been resolved differently.");
        }

        Estado = estado;
        ResueltaPorUsuarioId = resueltaPorUsuarioId;
        ResueltaEnUtc = resueltaEnUtc;
        MotivoResolucion = cleanedReason;
        Version = checked(Version + 1);
        return true;
    }

    private static string ValidarMotivo(string motivo, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length > 2000)
            throw new ArgumentException("A reason between 1 and 2000 characters is required.", parameterName);
        return motivo.Trim();
    }

    private static void ValidarUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
    }
}
