using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NexoRuta.IntegrationTests.Operacion;

// An actual loopback WebSocket gateway: connections alternate between two Kestrel
// APIs; a refused upstream is retried on the survivor. No sticky cookie or Redis.
internal sealed class OperationalWebSocketProxy(WebApplication host, Uri address,
    ConcurrentBag<string> selected, Func<int> failovers) : IAsyncDisposable
{
    public Uri Address => address;
    public IEnumerable<string> Selected => selected;
    public int Failovers => failovers();

    public static async Task<OperationalWebSocketProxy> StartAsync(Uri first, Uri second, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var selected = new ConcurrentBag<string>();
        var next = -1; var failures = 0;
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            var primary = Interlocked.Increment(ref next) % 2 == 0 ? first : second;
            ClientWebSocket? upstream = null;
            foreach (var destination in new[] { primary, primary == first ? second : first })
            {
                upstream = new ClientWebSocket();
                upstream.Options.SetRequestHeader("Authorization", context.Request.Headers.Authorization.ToString());
                var uri = new UriBuilder(new Uri(destination, "hubs/operacion")) { Scheme = "ws" }.Uri;
                try { await upstream.ConnectAsync(uri, context.RequestAborted); selected.Add(destination.Authority); break; }
                catch (WebSocketException) { Interlocked.Increment(ref failures); upstream.Dispose(); upstream = null; }
            }
            if (upstream is null) { context.Response.StatusCode = 502; return; }
            using (upstream)
            using (var downstream = await context.WebSockets.AcceptWebSocketAsync())
            {
                try { await Task.WhenAll(Pump(upstream, downstream, context.RequestAborted), Pump(downstream, upstream, context.RequestAborted)); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (WebSocketException) { }
            }
        });
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new Uri(address), selected, () => Volatile.Read(ref failures));
    }

    private static async Task Pump(WebSocket source, WebSocket target, CancellationToken ct)
    {
        var buffer = new byte[16384];
        while (source.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var message = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (message.MessageType == WebSocketMessageType.Close)
            {
                if (target.State == WebSocketState.Open)
                    await target.CloseOutputAsync(message.CloseStatus ?? WebSocketCloseStatus.NormalClosure, message.CloseStatusDescription, ct);
                return;
            }
            await target.SendAsync(new ArraySegment<byte>(buffer, 0, message.Count), message.MessageType, message.EndOfMessage, ct);
        }
    }
    public async ValueTask DisposeAsync() { await host.StopAsync(); await host.DisposeAsync(); }
}
