using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NexoRuta.Infrastructure.Webhooks;

public sealed class WebhookSecrets(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("NexoRuta.CommerceWebhooks.Secrets.v1");
    public string Protect(string secret) { Validate(secret); return protector.Protect(secret); }
    public string Unprotect(string value) => protector.Unprotect(value);
    public static byte[] Validate(string secret)
    {
        try
        {
            if (!secret.StartsWith("whsec_", StringComparison.Ordinal)) throw new FormatException();
            var key = Convert.FromBase64String(secret[6..]);
            if (key.Length != 32) throw new FormatException();
            return key;
        }
        catch (FormatException) { throw new ArgumentException("Usá una clave whsec_ de 32 bytes aleatorios codificados en Base64."); }
    }
}

/// <summary>Standard Webhooks symmetric signature, https://www.standardwebhooks.com/.</summary>
public static class WebhookSignature
{
    public static string Sign(string secret, string id, string timestamp, byte[] body)
    {
        if (!Guid.TryParse(id, out _) || !long.TryParse(timestamp, out _)) throw new ArgumentException("Invalid signed metadata.");
        var key = WebhookSecrets.Validate(secret);
        try
        {
            var prefix = Encoding.UTF8.GetBytes(id + "." + timestamp + ".");
            using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            mac.AppendData(prefix); mac.AppendData(body);
            return "v1," + Convert.ToBase64String(mac.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

public sealed class WebhookDestinationPolicy(IConfiguration config, IHostEnvironment environment)
{
    public Uri Validate(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("Indicá una URL HTTP(S) sin credenciales, consulta ni fragmento.");
        if (IsDemo(uri)) return uri;
        var allowed = config.GetSection("Webhooks:AllowedOrigins").Get<string[]>() ?? [];
        if (uri.Scheme != "https" || !allowed.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("El destino no está autorizado. Solicitá habilitar su origen HTTPS; el receptor de prueba tiene una excepción de desarrollo específica.");
        return uri;
    }
    public bool IsDemo(Uri uri) => (environment.IsDevelopment() || environment.IsEnvironment("Testing")) &&
        Uri.TryCreate(config["Webhooks:DevelopmentReceiverUrl"], UriKind.Absolute, out var demo) &&
        uri == demo && uri.AbsolutePath == "/comercio/webhook";
    public SocketsHttpHandler CreateHandler(Uri destination) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, MaxResponseHeadersLength = 16,
        ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, ct) =>
        {
            if (context.DnsEndPoint.Host != destination.DnsSafeHost || context.DnsEndPoint.Port != destination.Port)
                throw new HttpRequestException("Destination changed.");
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || !IsDemo(destination) && addresses.Any(a => !IsPublic(a)))
                throw new HttpRequestException("Destination address rejected.");
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, destination.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                catch (Exception ex) { socket.Dispose(); last = ex; }
            }
            throw new HttpRequestException("Connection failed.", last);
        }
    };
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is > 0 and < 224 && b[0] is not (10 or 127) &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] is 0 or 168) &&
            !(b[0] == 198 && b[1] is 18 or 19 or 51) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}
