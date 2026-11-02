using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Estimaciones;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Reparto;
public sealed class GeoapifyRoutingOptions
{
    public AdmisionGpsOptions Gps { get; init; } = new();
    public bool PermitirLlamadasReales{get;init;}public int MaxSolicitudesDiaGlobal{get;init;}=1000;public int MaxSolicitudesDiaOperador{get;init;}=100;public int MaxTramosCalculo{get;init;}=51;public int SolicitudesPorSegundo{get;init;}=4;public int TimeoutSegundos{get;init;}=8;
    public static GeoapifyRoutingOptions Leer(IConfiguration c)
    {
        var o=c.GetSection("RoutingGeoapify").Get<GeoapifyRoutingOptions>()??new();
        if(o.Gps.AntiguedadMaximaSegundos <1 || !double.IsFinite(o.Gps.PrecisionMaximaMetros) || o.Gps.PrecisionMaximaMetros is <=0 or >100000 || o.MaxSolicitudesDiaGlobal<1||o.MaxSolicitudesDiaOperador<1||o.MaxTramosCalculo<1||o.MaxTramosCalculo>501||o.SolicitudesPorSegundo is <1 or >5||o.TimeoutSegundos is <1 or >30)throw new InvalidOperationException("Límites RoutingGeoapify inválidos.");return o;
    }
}
public interface IConsumoRouting {Task<bool> EsperarAsync(CancellationToken ct);}
public sealed class ConsumoRouting(NexoRutaDbContext db,IUsuarioActual usuario,GeoapifyRoutingOptions opciones,TimeProvider reloj):IConsumoRouting
{
    public async Task<bool> EsperarAsync(CancellationToken ct)
    {
        var actor=await usuario.ObtenerAsync(ct);if(actor.OperadorId is not {} op||actor.ComercioId!=null)return false;
        double? demora;
        await using var aislado=new NexoRutaDbContext((DbContextOptions<NexoRutaDbContext>)db.GetService<IDbContextOptions>());
        await using(var tx=await aislado.BeginOrJoinAsync(ct))
        {
            await aislado.ApplyAsync(RlsActorContext.From(actor),ct);
            demora=await aislado.Database.SqlQuery<double?>($"SELECT nexoruta_app.reserve_routing_request({op},{opciones.MaxSolicitudesDiaGlobal},{opciones.MaxSolicitudesDiaOperador},{opciones.SolicitudesPorSegundo}) AS \"Value\"").SingleAsync(ct);await tx.CommitAsync(ct);
        }
        if(demora is null)return false;if(demora.Value>0)await Task.Delay(TimeSpan.FromSeconds(demora.Value),reloj,ct);return true;
    }
}
public sealed class GeoapifyRouting(IHttpClientFactory clientes,IConfiguration configuracion,GeoapifyRoutingOptions opciones,IConsumoRouting consumo,TimeProvider reloj):IProveedorTramosViales
{
    public const string ClienteHttp="RoutingGeoapify";
    public async Task<RespuestaTramoVial> CalcularAsync(SolicitudTramoVial p,CancellationToken ct)
    {
        RespuestaTramoVial Fallo(EstadoTramoVial e)=>new(p,e);
        ct.ThrowIfCancellationRequested();var key=configuracion["RoutingGeoapify:ApiKey"];
        if(!opciones.PermitirLlamadasReales||string.IsNullOrWhiteSpace(key))return Fallo(EstadoTramoVial.NoConfigurado);
        if(p.PerfilVial!="drive"||!p.Origen.EsValido||!p.Destino.EsValido)return Fallo(EstadoTramoVial.RespuestaInvalida);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(opciones.TimeoutSegundos));
        try
        {
            if(!await consumo.EsperarAsync(timeout.Token))return Fallo(EstadoTramoVial.CuotaExcedida);
            string Punto(PuntoVial q)=>q.Latitud.ToString("R",CultureInfo.InvariantCulture)+","+q.Longitud.ToString("R",CultureInfo.InvariantCulture);
            // Dedicated key, fixed host, no retries. The named client removes loggers; hosting does not register outbound HTTP spans.
            using var http=clientes.CreateClient(ClienteHttp);
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.geoapify.com/v1/routing?mode=drive&traffic=free_flow&units=metric&format=json&waypoints="+Uri.EscapeDataString(Punto(p.Origen)+"|"+Punto(p.Destino))+"&apiKey="+Uri.EscapeDataString(key));
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(response.StatusCode==HttpStatusCode.TooManyRequests)return Fallo(EstadoTramoVial.CuotaExcedida);
            if((int)response.StatusCode>=500||response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)return Fallo(EstadoTramoVial.ProveedorNoDisponible);
            await response.Content.LoadIntoBufferAsync(65536,timeout.Token);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));var root=json.RootElement;
            if(!response.IsSuccessStatusCode)
            {if(root.TryGetProperty("message",out var msg)&&msg.ValueKind==JsonValueKind.String&&msg.GetString()!.Contains("route not found",StringComparison.OrdinalIgnoreCase))return Fallo(EstadoTramoVial.RutaNoEncontrada);return Fallo(EstadoTramoVial.RespuestaInvalida);}
            var results=root.GetProperty("results");if(results.GetArrayLength()==0)return Fallo(EstadoTramoVial.RutaNoEncontrada);
            if(results.GetArrayLength()!=1)return Fallo(EstadoTramoVial.RespuestaInvalida);var result=results[0];var legs=result.GetProperty("legs");
            if(legs.GetArrayLength()!=1)return Fallo(EstadoTramoVial.RespuestaInvalida);
            if(result.TryGetProperty("distance_units",out var units)&&!string.Equals(units.GetString(),"meters",StringComparison.OrdinalIgnoreCase))return Fallo(EstadoTramoVial.RespuestaInvalida);
            var seconds=legs[0].GetProperty("time").GetDouble();var meters=legs[0].GetProperty("distance").GetDouble();
            if(!double.IsFinite(seconds)||seconds<0||!double.IsFinite(meters)||meters<0)return Fallo(EstadoTramoVial.RespuestaInvalida);
            return new(p,EstadoTramoVial.Encontrado,seconds,meters,new("Geoapify","drive/free_flow",FuenteVial.ProveedorVial,ModeloTrafico.SinTrafico,"Powered by Geoapify · © OpenStreetMap contributors",reloj.GetUtcNow(),null));
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return Fallo(EstadoTramoVial.TiempoAgotado);}
        catch(HttpRequestException){return Fallo(EstadoTramoVial.ProveedorNoDisponible);}
        catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException){return Fallo(EstadoTramoVial.RespuestaInvalida);}
    }
}
