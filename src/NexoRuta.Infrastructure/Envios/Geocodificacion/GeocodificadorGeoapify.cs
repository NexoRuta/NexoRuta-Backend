using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NexoRuta.Infrastructure.Hosting;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Application.Envios.Interfaces;

namespace NexoRuta.Infrastructure.Envios.Geocodificacion;

public sealed class GeocodificadorGeoapify(IHttpClientFactory clientes, IConfiguration configuracion, IMemoryCache cache,
    IConsumoGeocodificacion? consumo = null) : IGeocodificador
{
    public const string ClienteHttp = "GeocodificacionGeoapify";
    public const string ClaveConfiguracion = "Geocoding:Geoapify:ApiKey";
    public string Atribucion => "Powered by Geoapify · © OpenStreetMap contributors";

    public async Task<IReadOnlyList<PropuestaDestinoDatos>> BuscarAsync(string direccion, CancellationToken ct, bool autocompletar = false)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(direccion) || direccion.Trim().Length > 240) throw new ArgumentException("La dirección es obligatoria y admite hasta 240 caracteres.");
        if (autocompletar && direccion.Trim().Length < 3) return [];
        var options = OpcionesGeocodificacion.Leer(configuracion);
        var clave = configuracion[ClaveConfiguracion];
        if (!configuracion.GetValue<bool>("Geocoding:Geoapify:PermitirLlamadasReales") || string.IsNullOrWhiteSpace(clave) || consumo is null)
            throw new GeocodificacionNoDisponibleException();
        // El caché solo contiene datos geográficos de direcciones solicitadas, nunca contactos ni credenciales.
        var operacion = autocompletar ? "autocomplete" : "search";
        var claveCache = "geoapify:" + operacion + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(direccion.Trim())));
        if (cache.TryGetValue<IReadOnlyList<PropuestaDestinoDatos>>(claveCache, out var guardadas))
        {
            CacheTelemetry.Hit();
            return guardadas!;
        }
        CacheTelemetry.Miss();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSegundos));
            var externalCt = timeout.Token;
            if (cache.TryGetValue("geoapify:quota-exhausted", out _)) throw new GeocodificacionLimiteException(true);
            await consumo.ReservarAsync(externalCt);
            using var http = clientes.CreateClient(ClienteHttp);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.geoapify.com/v1/geocode/{operacion}?text={Uri.EscapeDataString(direccion.Trim())}&format=json&lang=es&limit=5&apiKey={Uri.EscapeDataString(clave)}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, externalCt);
            if ((int)response.StatusCode is 402 or 429)
            {
                cache.Set("geoapify:quota-exhausted", true, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(24)).SetSize(1));
                throw new GeocodificacionLimiteException(true);
            }
            if (!response.IsSuccessStatusCode) throw new GeocodificacionNoDisponibleException();
            await response.Content.LoadIntoBufferAsync(65_536, externalCt);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(externalCt));
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new GeocodificacionNoDisponibleException();
            var propuestas = new List<PropuestaDestinoDatos>();
            foreach (var item in results.EnumerateArray().Take(5))
            {
                if (!item.TryGetProperty("formatted", out var nombre) || nombre.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nombre.GetString())
                    || nombre.GetString()!.Length > 512
                    || !item.TryGetProperty("lat", out var lat) || lat.ValueKind != JsonValueKind.Number || !lat.TryGetDouble(out var latitud)
                    || !item.TryGetProperty("lon", out var lon) || lon.ValueKind != JsonValueKind.Number || !lon.TryGetDouble(out var longitud)
                    || !double.IsFinite(latitud) || latitud is < -90 or > 90 || !double.IsFinite(longitud) || longitud is < -180 or > 180)
                    throw new GeocodificacionNoDisponibleException();
                propuestas.Add(new(nombre.GetString()!, latitud, longitud));
            }
            var salida = propuestas.ToArray();
            cache.Set<IReadOnlyList<PropuestaDestinoDatos>>(claveCache, salida, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(24)).SetSize(1));
            return salida;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        { throw new GeocodificacionNoDisponibleException(); }
    }
}

