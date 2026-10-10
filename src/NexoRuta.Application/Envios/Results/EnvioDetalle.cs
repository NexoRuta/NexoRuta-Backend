using NexoRuta.Application.Envios.Dtos;
namespace NexoRuta.Application.Envios.Results;

public sealed record EnvioDetalle(
    Guid Id,
    OrigenEnvio Origen,
    string DestinatarioNombre,
    string Direccion,
    string Estado,
    IReadOnlyList<BultoDetalle> Bultos);
