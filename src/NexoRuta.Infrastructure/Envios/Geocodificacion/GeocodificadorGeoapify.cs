using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Application.Envios.Interfaces;

namespace NexoRuta.Infrastructure.Envios.Geocodificacion;

public sealed class GeocodificadorGeoapify(IHttpClientFactory clientes, IConfiguration configuracion, IMemoryCache cache) : IGeocodificador
{
    public const string ClienteHttp = "GeocodificacionGeoapify";
    public const string ClaveConfiguracion = "Geocoding:Geoapify:ApiKey";
    public string Atribucion => "Powered by Geoapify · © OpenStreetMap contributors";

    public async Task<IReadOnlyList<PropuestaDestinoDatos>> BuscarAsync(string direccion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(direccion) || direccion.Trim().Length > 240) throw new ArgumentException("La dirección es obligatoria y admite hasta 240 caracteres.");
        var clave = configuracion[ClaveConfiguracion];
        if (string.IsNullOrWhiteSpace(clave)) throw new GeocodificacionNoDisponibleException();
        // El caché solo contiene datos geográficos de direcciones solicitadas, nunca contactos ni credenciales.
        var claveCache = "geoapify:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(direccion.Trim())));
        if (cache.TryGetValue<IReadOnlyList<PropuestaDestinoDatos>>(claveCache, out var guardadas)) return guardadas!;
        try
        {
            using var http = clientes.CreateClient(ClienteHttp);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.geoapify.com/v1/geocode/search?text={Uri.EscapeDataString(direccion.Trim())}&format=json&lang=es&limit=5&apiKey={Uri.EscapeDataString(clave)}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new GeocodificacionNoDisponibleException();
            await response.Content.LoadIntoBufferAsync(65_536, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new GeocodificacionNoDisponibleException();
            var propuestas = new List<PropuestaDestinoDatos>();
            foreach (var item in results.EnumerateArray().Take(5))
            {
                if (!item.TryGetProperty("formatted", out var nombre) || nombre.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nombre.GetString())
                    || !item.TryGetProperty("lat", out var lat) || !lat.TryGetDouble(out var latitud)
                    || !item.TryGetProperty("lon", out var lon) || !lon.TryGetDouble(out var longitud)
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
