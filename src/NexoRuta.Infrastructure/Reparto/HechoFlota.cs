namespace NexoRuta.Infrastructure.Reparto;

public sealed class HechoFlota
{
    public Guid OperadorId { get; init; }
    public Guid ComandoId { get; init; }
    public Guid UsuarioId { get; init; }
    public Guid AccesoId { get; init; }
    public Guid VehiculoId { get; init; }
    public Guid? RutaId { get; init; }
    public Guid? OcupacionId { get; init; }
    public string Accion { get; init; } = "";
    public string Hash { get; init; } = "";
    public string Motivo { get; init; } = "";
    public long VersionAnterior { get; init; }
    public DateTimeOffset ConfirmadoEnUtc { get; init; }
    public string Respuesta { get; init; } = "";
}
