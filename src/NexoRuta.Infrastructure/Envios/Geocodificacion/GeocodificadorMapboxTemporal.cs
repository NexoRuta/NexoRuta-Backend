using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Application.Envios.Interfaces;

namespace NexoRuta.Infrastructure.Envios.Geocodificacion;

// Same v6 forward endpoint and feature mapping as the existing selector, now behind the shared project budget.
public sealed class GeocodificadorMapboxTemporal(IHttpClientFactory clientes, IConfiguration configuration,
    IConsumoGeocodificacion consumo) : IGeocodificador
{
    public const string ClienteHttp = "GeocodificacionMapboxTemporal";
    public const string Servicio = "destino-mapbox-temporal";
    public string Atribucion => "© Mapbox — resultados temporales, no persistibles";

    public async Task<IReadOnlyList<PropuestaDestinoDatos>> BuscarAsync(string direccion, CancellationToken ct, bool autocompletar = false)
    {
        ct.ThrowIfCancellationRequested();
        var query = direccion?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > 240) throw new ArgumentException("La búsqueda admite de 3 a 240 caracteres.");
        if (query.Length < 3) return [];
        var token = configuration["Mapbox:PublicAccessToken"];
        if (!configuration.GetValue<bool>("Geocoding:MapboxTemporal:Habilitado") || token?.StartsWith("pk.", StringComparison.Ordinal) != true)
            throw new GeocodificacionNoDisponibleException();
        var options = OpcionesGeocodificacion.Leer(configuration);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSegundos));
            // Independent committed reservation is never refunded, even if the caller subsequently cancels.
            await consumo.ReservarAsync(timeout.Token);
            using var http = clientes.CreateClient(ClienteHttp);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.mapbox.com/search/geocode/v6/forward?q=" + Uri.EscapeDataString(query)
                + "&permanent=false&autocomplete=" + (autocompletar ? "true" : "false")
                + "&limit=5&language=es&types=address,street,place,locality,neighborhood&access_token=" + Uri.EscapeDataString(token));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is 402 or 429) throw new GeocodificacionLimiteException(true);
            if (!response.IsSuccessStatusCode) throw new GeocodificacionNoDisponibleException();
            await response.Content.LoadIntoBufferAsync(65_536, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                throw new GeocodificacionNoDisponibleException();
            var result = new List<PropuestaDestinoDatos>();
            foreach (var feature in features.EnumerateArray().Take(5))
            {
                var coordinates = feature.GetProperty("geometry").GetProperty("coordinates");
                var properties = feature.GetProperty("properties");
                var name = properties.TryGetProperty("full_address", out var address) ? address.GetString()
                    : properties.TryGetProperty("name", out var fallback) ? fallback.GetString() : null;
                var lon = coordinates[0].GetDouble(); var lat = coordinates[1].GetDouble();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || !double.IsFinite(lon) || lon is < -180 or > 180
                    || !double.IsFinite(lat) || lat is < -90 or > 90) throw new GeocodificacionNoDisponibleException();
                result.Add(new(name, lat, lon));
            }
            // No result cache: temporary Mapbox results are only returned for the current interactive preview.
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException
            or KeyNotFoundException or IndexOutOfRangeException or ArgumentOutOfRangeException or OperationCanceledException)
        { throw new GeocodificacionNoDisponibleException(); }
    }
}
