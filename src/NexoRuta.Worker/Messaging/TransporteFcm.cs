using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
namespace NexoRuta.Worker.Messaging;
public sealed class TransporteFcm : ITransporteFcm, IDisposable
{
    private readonly FirebaseApp app;
    private readonly FirebaseMessaging mensajeria;
    public TransporteFcm(IConfiguration configuration)
    {
        var proyecto = configuration["Push:ProjectId"];
        if (string.IsNullOrWhiteSpace(proyecto)) throw new InvalidOperationException("Falta Push:ProjectId.");
        app = FirebaseApp.Create(new AppOptions { ProjectId = proyecto, Credential = GoogleCredential.GetApplicationDefault() }, "nexoruta-push");
        mensajeria = FirebaseMessaging.GetMessaging(app);
    }
    public async Task<IReadOnlyList<ResultadoEnvioPush>> EnviarAsync(MulticastMessage mensaje, CancellationToken ct)
    {
        BatchResponse respuesta;
        try
        {
            respuesta = await mensajeria.SendEachForMulticastAsync(mensaje, ct);
        }
        catch (FirebaseMessagingException ex)
        {
            // Un error de lote no identifica qué FID falló; no desactivar todas las instalaciones.
            var fallo = ResultadoEnvioPush.DesdeErrorDeLote(ex);
            return Enumerable.Repeat(fallo, (mensaje.Fids ?? []).Count).ToArray();
        }

        return respuesta.Responses.Select(r => r.IsSuccess
            ? new ResultadoEnvioPush(EstadoEnvioPush.Aceptado, r.MessageId)
            : r.Exception?.MessagingErrorCode == MessagingErrorCode.Unregistered
                ? new ResultadoEnvioPush(EstadoEnvioPush.RegistroInvalido)
                : ResultadoEnvioPush.DesdeError(r.Exception)).ToArray();
    }
    public void Dispose() => app.Delete();
}
