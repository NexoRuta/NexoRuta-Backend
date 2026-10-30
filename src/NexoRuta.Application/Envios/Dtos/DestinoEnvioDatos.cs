namespace NexoRuta.Application.Envios.Dtos;

public sealed record DestinoEnvioDatos(Guid EnvioId, string DestinatarioNombre, string Direccion, long Version, string Estado,
    double? Latitud, double? Longitud, DateTimeOffset? ConfirmadaEnUtc);
