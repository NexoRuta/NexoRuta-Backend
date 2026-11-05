using NexoRuta.Application.Envios.Dtos;
namespace NexoRuta.Application.Envios.Results;

public sealed record EnvioDetalle(
    Guid Id,
    OrigenEnvio Origen,
    string DestinatarioNombre,
    string Direccion,
    string Estado,
    long Version,
    IReadOnlyList<BultoDetalle> Bultos,
    DateTimeOffset CreadoEnUtc)
{
    public NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa? Tarifa { get; init; }
    public Guid? SolicitudRetiroId { get; init; }
    public string? SolicitudRetiroHuella { get; init; }
    public bool EsRetiro { get; init; }
    public string? MotivoRetiro { get; init; }
    public DateTimeOffset? FranjaRetiroDesdeUtc { get; init; }
    public DateTimeOffset? FranjaRetiroHastaUtc { get; init; }
    public bool DestinoConfirmado { get; init; }
    public string? ReferenciaExterna { get; init; }
    public string DestinatarioEmail { get; init; } = string.Empty;
}
