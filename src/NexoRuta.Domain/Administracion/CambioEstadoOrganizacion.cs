namespace NexoRuta.Domain.Administracion;

public sealed class CambioEstadoOrganizacion
{
    public Guid Id { get; private set; }
    public Guid? OperadorId { get; private set; }
    public Guid? ComercioId { get; private set; }
    public TipoAccesoUsuario TipoOrganizacion { get; private set; }
    public Guid OrganizacionId { get; private set; }
    public EstadoOrganizacion EstadoAnterior { get; private set; }
    public EstadoOrganizacion EstadoNuevo { get; private set; }
    public Guid ActorUsuarioId { get; private set; }
    public DateTimeOffset OcurridoEnUtc { get; private set; }
    public string Motivo { get; private set; } = "";
    public Guid? SolicitudBajaId { get; private set; }

    private CambioEstadoOrganizacion() { } // EF Core

    public CambioEstadoOrganizacion(
        TipoAccesoUsuario tipoOrganizacion,
        Guid organizacionId,
        EstadoOrganizacion estadoAnterior,
        EstadoOrganizacion estadoNuevo,
        Guid actorUsuarioId,
        DateTimeOffset ocurridoEnUtc,
        string motivo,
        Guid? solicitudBajaId = null)
    {
        if (tipoOrganizacion is not (TipoAccesoUsuario.Operador or TipoAccesoUsuario.Comercio))
            throw new ArgumentOutOfRangeException(nameof(tipoOrganizacion));
        if (organizacionId == Guid.Empty || actorUsuarioId == Guid.Empty)
            throw new ArgumentException("An organization and actor are required.");
        if (estadoAnterior == estadoNuevo)
            throw new ArgumentException("A state change must change the organization state.");
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length > 2000)
            throw new ArgumentException("A reason between 1 and 2000 characters is required.", nameof(motivo));
        if (ocurridoEnUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The timestamp must be UTC.", nameof(ocurridoEnUtc));

        Id = Guid.CreateVersion7();
        TipoOrganizacion = tipoOrganizacion;
        OrganizacionId = organizacionId;
        OperadorId = tipoOrganizacion == TipoAccesoUsuario.Operador ? organizacionId : null;
        ComercioId = tipoOrganizacion == TipoAccesoUsuario.Comercio ? organizacionId : null;
        EstadoAnterior = estadoAnterior;
        EstadoNuevo = estadoNuevo;
        ActorUsuarioId = actorUsuarioId;
        OcurridoEnUtc = ocurridoEnUtc;
        Motivo = motivo.Trim();
        SolicitudBajaId = solicitudBajaId;
    }
}
