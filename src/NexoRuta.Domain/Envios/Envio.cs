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

    public void DeclararEntregaFisica(DateTimeOffset instante, bool pruebaCompleta, long versionEsperada)
    {
        if (Estado != EstadoEnvio.EnTransito)
            throw new InvalidOperationException("La entrega física requiere el envío en tránsito y su versión actual.");
        RegistrarEntregaFisica(instante, pruebaCompleta, versionEsperada);
    }

    public void ConciliarEntregaFisica(DateTimeOffset instante, bool pruebaCompleta, long versionEsperada)
    {
        if (Estado is not (EstadoEnvio.Cancelado or EstadoEnvio.Reprogramado or EstadoEnvio.AsignadoRuta))
            throw new InvalidOperationException("La conciliación requiere una cancelación o reprogramación concurrente con la entrega física.");
        RegistrarEntregaFisica(instante, pruebaCompleta, versionEsperada);
    }

    private void RegistrarEntregaFisica(DateTimeOffset instante, bool pruebaCompleta, long versionEsperada)
    {
        if (instante == default || EntregaFisicaEnUtc is not null)
            throw new InvalidOperationException("La declaración física requiere una fecha y no puede reemplazar otra entrega.");
        if (Version != versionEsperada) throw new InvalidOperationException("El envío cambió desde la última consulta.");
        EntregaFisicaEnUtc = instante.ToUniversalTime();
        Estado = pruebaCompleta ? EstadoEnvio.Entregado : EstadoEnvio.EntregaPendienteValidacion;
        Version = checked(Version + 1);
    }

    public void RegistrarProgresoPrueba(long versionEsperada)
    {
        if (Version != versionEsperada || Estado != EstadoEnvio.EntregaPendienteValidacion)
            throw new InvalidOperationException("La entrega ya no está pendiente de prueba en esa versión.");
        Version++;
    }

    private static bool Permite(EstadoEnvio origen, EstadoEnvio destino) => (origen, destino) switch
    {
        (EstadoEnvio.Admitido, EstadoEnvio.EnDeposito or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnDeposito, EstadoEnvio.AsignadoRuta or EstadoEnvio.EnDevolucion or EstadoEnvio.Extraviado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.AsignadoRuta, EstadoEnvio.EnTransito or EstadoEnvio.EnDeposito or EstadoEnvio.Reprogramado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnTransito, EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion or EstadoEnvio.NoEntregado or EstadoEnvio.Reprogramado or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.NoEntregado, EstadoEnvio.EnTransito or EstadoEnvio.Reprogramado or EstadoEnvio.EnDevolucion or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.Reprogramado, EstadoEnvio.AsignadoRuta or EstadoEnvio.EnDevolucion or EstadoEnvio.Cancelado) => true,
        (EstadoEnvio.EnDevolucion, EstadoEnvio.Devuelto) => true,
        (EstadoEnvio.EntregaPendienteValidacion, EstadoEnvio.Entregado or EstadoEnvio.EntregaSinValidacion or EstadoEnvio.EnDevolucion) => true,
        (EstadoEnvio.Entregado or EstadoEnvio.EntregaSinValidacion, EstadoEnvio.EnDevolucion) => true,
        _ => false
    };
}
