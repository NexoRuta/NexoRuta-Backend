namespace NexoRuta.Application.Envios;

public sealed record EnvioDetalle(
    Guid Id,
    OrigenEnvio Origen,
    string DestinatarioNombre,
    string Direccion,
    string Estado,
    IReadOnlyList<BultoDetalle> Bultos);
