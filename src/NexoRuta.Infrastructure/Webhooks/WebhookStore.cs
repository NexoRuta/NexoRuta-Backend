using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Webhooks;

public sealed class WebhookSubscription
{
    public Guid Id { get; set; }
    public Guid OperadorId { get; set; }
    public Guid ComercioId { get; set; }
    public string Url { get; set; } = "";
    public string[] Eventos { get; set; } = [];
    public int Contrato { get; set; }
    public bool Habilitada { get; set; }
    public bool Revocada { get; set; }
    public long Revision { get; set; }
    public Guid ClaveId { get; set; }
    public DateTimeOffset ActualizadaEnUtc { get; set; }
}
public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid EventoId { get; set; }
    public Guid SuscripcionId { get; set; }
    public long Revision { get; set; }
    public Guid OperadorId { get; set; }
    public Guid ComercioId { get; set; }
    public Guid EnvioId { get; set; }
    public string Tipo { get; set; } = "";
    public int Contrato { get; set; }
    public string Url { get; set; } = "";
    public string Payload { get; set; } = "";
    public Guid ClaveId { get; set; }
    public string Estado { get; set; } = "";
    public int Intentos { get; set; }
    public int IntentosCiclo { get; set; }
    public int Reenvios { get; set; }
    public DateTimeOffset ProximoIntentoUtc { get; set; }
    public DateTimeOffset CreadaEnUtc { get; set; }
    public DateTimeOffset? FinalizadaEnUtc { get; set; }
    public int? CodigoHttp { get; set; }
    public string? Diagnostico { get; set; }
}
public sealed class WebhookClaim
{
    public Guid Id { get; set; }
    public Guid EventoId { get; set; }
    public string Url { get; set; } = "";
    public string Payload { get; set; } = "";
    public string SecretoProtegido { get; set; } = "";
    public Guid ClaveId { get; set; }
    public int Contrato { get; set; }
    public string Tipo { get; set; } = "";
    public int IntentosCiclo { get; set; }
    public Guid? CorrelationId { get; set; }
    public string? TraceParent { get; set; }
}
public sealed record WebhookAttempt(Guid EntregaId, int Numero, DateTimeOffset InicioUtc, DateTimeOffset? FinUtc, int? CodigoHttp, string? Diagnostico);
public sealed record WebhookReplay(Guid EntregaId, int Numero, Guid ActorId, DateTimeOffset FechaUtc, string EstadoAnterior);
public sealed class WebhookStore(NexoRutaDbContext db)
{
    public const string SubscriptionColumns = "\"Id\",\"OperadorId\",\"ComercioId\",\"Url\",\"Eventos\",\"Contrato\",\"Habilitada\",\"Revocada\",\"Revision\",\"ClaveId\",\"ActualizadaEnUtc\"";
    public const string DeliveryColumns = "\"Id\",\"EventoId\",\"SuscripcionId\",\"Revision\",\"OperadorId\",\"ComercioId\",\"EnvioId\",\"Tipo\",\"Contrato\",\"Url\",\"Payload\",\"ClaveId\",\"Estado\",\"Intentos\",\"IntentosCiclo\",\"Reenvios\",\"ProximoIntentoUtc\",\"CreadaEnUtc\",\"FinalizadaEnUtc\",\"CodigoHttp\",\"Diagnostico\"";
    public IQueryable<WebhookSubscription> Subscriptions => db.Database.SqlQueryRaw<WebhookSubscription>("SELECT " + SubscriptionColumns + " FROM public.\"WebhookSubscriptions\"");
    public IQueryable<WebhookDelivery> Deliveries => db.Database.SqlQueryRaw<WebhookDelivery>("SELECT " + DeliveryColumns + " FROM public.\"WebhookDeliveries\"");
    public Task<WebhookClaim?> Claim(Guid token, int maxAttempts, CancellationToken ct) => db.Database.SqlQuery<WebhookClaim>($"""
        SELECT "Id","EventoId","Url","Payload","SecretoProtegido","ClaveId","Contrato","Tipo","IntentosCiclo","CorrelationId","TraceParent" FROM nexoruta_app.webhook_claim({token},{maxAttempts})
        """).SingleOrDefaultAsync(ct);
    public Task<bool> Finish(Guid id, Guid token, int? code, string? diagnostic, bool retry, int delay, CancellationToken ct) => db.Database.SqlQuery<bool>($"""
        SELECT nexoruta_app.webhook_finish({id},{token},{code},{diagnostic},{retry},{delay}) AS "Value"
        """).SingleAsync(ct);
}
