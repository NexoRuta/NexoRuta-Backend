namespace NexoRuta.Domain.Envios;

public sealed class Envio
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid ComercioId { get; private set; }
    public Guid CreadoPorUsuarioId { get; private set; }
    public Guid DestinatarioId { get; private set; }
    public Guid DireccionId { get; private set; }
    public Guid VersionReglasId { get; private set; }
    public EstadoEnvio Estado { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset CreadoEnUtc { get; private set; }
    public DateTimeOffset? EntregaFisicaEnUtc { get; private set; }
    public DateTimeOffset? EntregaFisicaRecibidaEnUtc { get; private set; }
    public DateTimeOffset? PlazoPruebaVenceEnUtc { get; private set; }

    public Envio(
        Guid operadorId,
        Guid comercioId,
        Guid creadoPorUsuarioId,
        Guid destinatarioId,
        Guid direccionId,
        Guid versionReglasId,
        DateTimeOffset creadoEnUtc = default)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (comercioId == Guid.Empty)
            throw new ArgumentException("El comercio es obligatorio.", nameof(comercioId));
        if (creadoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("El usuario creador es obligatorio.", nameof(creadoPorUsuarioId));
        if (destinatarioId == Guid.Empty)
            throw new ArgumentException("El destinatario es obligatorio.", nameof(destinatarioId));
        if (direccionId == Guid.Empty)
            throw new ArgumentException("La dirección es obligatoria.", nameof(direccionId));
        if (versionReglasId == Guid.Empty)
            throw new ArgumentException("El envío requiere una publicación de reglas operativas.", nameof(versionReglasId));

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        ComercioId = comercioId;
        CreadoPorUsuarioId = creadoPorUsuarioId;
        DestinatarioId = destinatarioId;
        DireccionId = direccionId;
        VersionReglasId = versionReglasId;
        var creado = creadoEnUtc == default ? DateTimeOffset.UtcNow : creadoEnUtc;
        if (creado.Offset != TimeSpan.Zero)
            throw new ArgumentException("La fecha de creación debe estar en UTC.", nameof(creadoEnUtc));
        CreadoEnUtc = creado;
        Estado = EstadoEnvio.Admitido;
    }

    public void CambiarEstado(EstadoEnvio destino, long versionEsperada)
    {
        if (destino is EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion && EntregaFisicaEnUtc is null)
            throw new InvalidOperationException("Debe declararse el hecho físico antes de validar la entrega.");
        if (Version != versionEsperada) throw new InvalidOperationException("El envío cambió desde la última consulta.");
        if (!Permite(Estado, destino) || destino == EstadoEnvio.Cancelado && EntregaFisicaEnUtc is not null)
            throw new InvalidOperationException("La transición no corresponde al estado actual del envío.");
        Estado = destino;
        Version = checked(Version + 1);
    }

    public void IniciarDevolucionPorDecision(long versionEsperada)
    {
        if (Estado != EstadoEnvio.NoEntregado)
            throw new InvalidOperationException("La decisión de devolución requiere un intento fallido sin estado posterior.");
        CambiarEstado(EstadoEnvio.EnDevolucion, versionEsperada);
    }

    public void ConfirmarExtravio(long versionEsperada)
        => CambiarEstado(EstadoEnvio.Extraviado, versionEsperada);

    public void RegistrarReasignacionSinCambioDeEstado(long versionEsperada)
    {
        if (Estado != EstadoEnvio.AsignadoRuta || Version != versionEsperada || EntregaFisicaEnUtc is not null)
            throw new InvalidOperationException("La reasignación sin transición requiere un envío asignado, sin entrega física y con su versión vigente.");
        Version = checked(Version + 1);
    }

    public void DeclararEntregaFisica(DateTimeOffset instanteFisico, DateTimeOffset recibidoEnUtc,
        int? plazoValidacionMinutos, bool pruebaCompleta, long versionEsperada)
    {
        if (Estado != EstadoEnvio.EnTransito)
            throw new InvalidOperationException("La entrega física requiere el envío en tránsito y su versión actual.");
        RegistrarEntregaFisica(instanteFisico, recibidoEnUtc, plazoValidacionMinutos, pruebaCompleta, versionEsperada);
    }

    public void ConciliarEntregaFisica(DateTimeOffset instanteFisico, DateTimeOffset recibidoEnUtc,
        int? plazoValidacionMinutos, bool pruebaCompleta, long versionEsperada)
    {
        if (Estado != EstadoEnvio.Cancelado)
            throw new InvalidOperationException("La conciliación tardía solo conserva una entrega física concurrente con una cancelación.");
        RegistrarEntregaFisica(instanteFisico, recibidoEnUtc, plazoValidacionMinutos, pruebaCompleta, versionEsperada);
    }

    private void RegistrarEntregaFisica(DateTimeOffset instanteFisico, DateTimeOffset recibidoEnUtc,
        int? plazoValidacionMinutos, bool pruebaCompleta, long versionEsperada)
    {
        if (instanteFisico == default || recibidoEnUtc == default || recibidoEnUtc.Offset != TimeSpan.Zero
            || plazoValidacionMinutos is < 1 or > ReglasOperativas.ReglasOperativas.MaxDurationMinutes
            || EntregaFisicaEnUtc is not null)
            throw new InvalidOperationException("La declaración física requiere una fecha y no puede reemplazar otra entrega.");
        if (Version != versionEsperada) throw new InvalidOperationException("El envío cambió desde la última consulta.");
        EntregaFisicaEnUtc = instanteFisico.ToUniversalTime();
        EntregaFisicaRecibidaEnUtc = recibidoEnUtc;
        PlazoPruebaVenceEnUtc = Estado == EstadoEnvio.Cancelado || plazoValidacionMinutos is null
            ? null
            : recibidoEnUtc.AddMinutes(plazoValidacionMinutos.Value);
        if (Estado != EstadoEnvio.Cancelado)
            Estado = pruebaCompleta ? EstadoEnvio.Entregado : EstadoEnvio.EntregaPendienteValidacion;
        Version = checked(Version + 1);
    }

    public bool RegistrarEvidenciaEntrega(DateTimeOffset recibidaEnUtc, bool pruebaCompleta, long versionEsperada)
    {
        if (Version != versionEsperada || Estado != EstadoEnvio.EntregaPendienteValidacion
            || EntregaFisicaEnUtc is null || recibidaEnUtc == default || recibidaEnUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("La prueba requiere una entrega física pendiente y la versión actual.");

        var dentroDelPlazo = PlazoPruebaVenceEnUtc is null || recibidaEnUtc < PlazoPruebaVenceEnUtc;
        if (pruebaCompleta && dentroDelPlazo) Estado = EstadoEnvio.Entregado;
        Version = checked(Version + 1);
        return pruebaCompleta && dentroDelPlazo;
    }

    public void RegistrarEvidenciaTardiaSinTransicion(long versionEsperada)
    {
        if (Version != versionEsperada || Estado is not (EstadoEnvio.EntregaPendienteValidacion or EstadoEnvio.EntregaSinValidacion)
            || EntregaFisicaEnUtc is null)
            throw new InvalidOperationException("La evidencia tardía requiere un hecho físico pendiente o vencido.");
        Version = checked(Version + 1);
    }

    public void RegistrarProgresoPrueba(long versionEsperada)
    {
        if (Version != versionEsperada || Estado != EstadoEnvio.EntregaPendienteValidacion)
            throw new InvalidOperationException("La entrega ya no está pendiente de prueba en esa versión.");
        Version++;
    }

    public void RegistrarPruebaTardiaTrasCancelacion(long versionEsperada)
    {
        if (Version != versionEsperada || Estado != EstadoEnvio.Cancelado || EntregaFisicaEnUtc is null)
            throw new InvalidOperationException("La prueba tardía requiere un hecho físico conciliado para un envío cancelado.");
        Version = checked(Version + 1);
    }

    private static bool Permite(EstadoEnvio origen, EstadoEnvio destino) => (origen, destino) switch
    {
        (EstadoEnvio.Admitido, EstadoEnvio.EnDeposito or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnTransito, EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion or EstadoEnvio.NoEntregado or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.NoEntregado, EstadoEnvio.Reprogramado or EstadoEnvio.EnDevolucion or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.Reprogramado, EstadoEnvio.AsignadoRuta or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnDevolucion, EstadoEnvio.Devuelto or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EntregaPendienteValidacion, EstadoEnvio.Entregado or EstadoEnvio.EntregaSinValidacion) => true,
        _ => false
    };
}
