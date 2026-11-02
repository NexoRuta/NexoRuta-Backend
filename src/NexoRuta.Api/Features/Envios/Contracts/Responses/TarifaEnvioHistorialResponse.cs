namespace NexoRuta.Api.Features.Envios.Contracts.Responses;
public sealed record TarifaEnvioHistorialResponse(Guid AplicacionId, DateTimeOffset AplicadaEnUtc,
    TarifaEnvioResponse Tarifa, double Latitud, double Longitud, long VersionZona, int RevisionZona);
