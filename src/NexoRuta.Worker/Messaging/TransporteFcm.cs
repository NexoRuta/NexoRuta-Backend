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
    public Task<BatchResponse> EnviarAsync(MulticastMessage mensaje, CancellationToken ct) => mensajeria.SendEachForMulticastAsync(mensaje, ct);
    public void Dispose() => app.Delete();
}
