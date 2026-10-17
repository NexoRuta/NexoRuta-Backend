using System.Globalization;
using FirebaseAdmin.Messaging;
using NexoRuta.Domain.Notificaciones;
namespace NexoRuta.Worker.Messaging;
public static class MensajePushReparto
{
    public static MulticastMessage Crear(AvisoReparto aviso, IReadOnlyList<string> fids, DateTimeOffset ahora) => new()
    {
        Fids = fids,
        Notification = new() { Title = "Cambio en tu hoja de ruta", Body = "Hay una entrega que debés revisar. Abrí NexoRuta." },
        Data = new Dictionary<string, string>
        {
            ["v"] = "1", ["notificacionId"] = aviso.Id.ToString(), ["operadorId"] = aviso.OperadorId.ToString(), ["accesoId"] = aviso.AccesoId.ToString()
        },
        Android = new() { Priority = Priority.High, TimeToLive = TimeSpan.FromHours(24),
            Notification = new() { ChannelId = "nexoruta.avisos", Icon = "ic_reparto", Tag = aviso.Id.ToString(), Sound = "default" } },
        Apns = new()
        {
            Headers = new Dictionary<string, string> { ["apns-push-type"] = "alert", ["apns-priority"] = "10",
                ["apns-collapse-id"] = aviso.Id.ToString(), ["apns-expiration"] = ahora.AddHours(24).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
            Aps = new() { Sound = "default" }
        }
    };
}
