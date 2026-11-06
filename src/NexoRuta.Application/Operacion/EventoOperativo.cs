using System.Text.Json;

namespace NexoRuta.Application.Operacion;

// Independent, additive contract. Published v1/v2 shipment contracts remain unchanged.
public sealed record EventoOperativo(
    int SchemaVersion, Guid EventId, Guid OperadorId, Guid? ComercioId,
    string Tipo, Guid EntidadId, Guid? EnvioId, Guid? RutaId, long Version,
    Guid CorrelacionId, DateTimeOffset? OcurridaEnUtc, DateTimeOffset RegistradaEnUtc,
    JsonElement Datos)
{
    public const string Topic = "nexoruta.operacion.v1";
    public const string OutboxType = "EventoOperativo";

    public void Validar()
    {
        if (SchemaVersion != 1 || EventId == Guid.Empty || OperadorId == Guid.Empty
            || EntidadId == Guid.Empty || CorrelacionId == Guid.Empty || Version < 0
            || RegistradaEnUtc == default || Datos.ValueKind != JsonValueKind.Object
            || Tipo is not ("Envio" or "Bulto" or "Posicion" or "Ruta" or "Parada" or "Estimacion" or "Flota"))
            throw new InvalidDataException("Invalid operational event contract.");
        if (ComercioId == Guid.Empty || EnvioId == Guid.Empty || RutaId == Guid.Empty)
            throw new InvalidDataException("Optional operational scope keys cannot be empty.");
        if (Tipo is "Envio" or "Bulto" or "Parada" && (EnvioId is null || EnvioId == Guid.Empty || ComercioId is null || ComercioId == Guid.Empty))
            throw new InvalidDataException("Shipment events require operator and commerce scope.");
        if (Tipo == "Envio" && EntidadId != EnvioId || Tipo is "Ruta" or "Estimacion" or "Posicion" && EntidadId != RutaId)
            throw new InvalidDataException("Entity identity disagrees with operational scope.");
        if (Tipo == "Posicion" && (RutaId is null || OcurridaEnUtc is null))
            throw new InvalidDataException("Position requires route and physical sample time.");
        if (Tipo == "Posicion" && (!Datos.TryGetProperty("latitud", out var lat) || !lat.TryGetDouble(out var latitude)
            || !Datos.TryGetProperty("longitud", out var lon) || !lon.TryGetDouble(out var longitude)
            || !Datos.TryGetProperty("precisionMetros", out var accuracy) || !accuracy.TryGetDouble(out var meters)
            || !double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(meters)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180 || meters <= 0))
            throw new InvalidDataException("Invalid physical position sample.");
    }
}
