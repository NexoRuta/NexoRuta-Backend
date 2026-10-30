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
    public bool DestinoConfirmado { get; init; }
    public string? ReferenciaExterna { get; init; }
    public string DestinatarioEmail { get; init; } = string.Empty;
}
