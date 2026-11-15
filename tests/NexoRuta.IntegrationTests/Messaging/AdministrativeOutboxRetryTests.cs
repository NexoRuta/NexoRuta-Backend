using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Security;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Worker.Messaging;
using Npgsql;
using Testcontainers.RabbitMq;

namespace NexoRuta.IntegrationTests.Messaging;

public sealed class AdministrativeOutboxRetryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/admin/mensajes-internos";

    [Fact]
    public async Task RetryRequiresOperatorPermissionsIsIsolatedAuditedAndAtomic()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        await using var owner = data.CreateDbContext();
        var op = await owner.AccesosUsuario.SingleAsync(a => a.OperadorId != null);
        var commerce = await owner.AccesosUsuario.SingleAsync(a => a.ComercioId != null);
        var shipment = await CreateShipment(data, commerce.Id);
        var message = await owner.OutboxMessages.SingleAsync(m => m.EnvioId == shipment && m.Tipo == "GenerarSeguimiento");
        await Fail(data, message.Id);
        var foreign = new AccesoInicial("retry-other@example.test", "Other operator", "Other commerce", "retry-other-op@example.test");
        await AuthenticationTestData.Seeder(owner, foreign).SeedAsync();
        var otherAccess = await owner.AccesosUsuario.SingleAsync(a => a.OperadorId == foreign.OperadorId);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = api.CreateHttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url)).StatusCode);
        await client.SetBearerForAccessAsync(data, commerce.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Url}/{message.Id}/reintentos", Request(1))).StatusCode);
        await client.SetBearerForAccessAsync(data, otherAccess.Id);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>(Url))!);
        Assert.Equal("NoDisponible", await Retry(client, message.Id, Request(1)));
        await client.SetBearerForAccessAsync(data, op.Id);
        var listing = (await client.GetFromJsonAsync<JsonElement[]>(Url))!;
        var row = Assert.Single(listing);
        Assert.Equal(message.Id, row.GetProperty("id").GetGuid());
        Assert.Equal("Error", row.GetProperty("estado").GetString());
        Assert.False(row.TryGetProperty("payload", out _));
        Assert.DoesNotContain("secret", row.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("NoElegible", await Retry(client, message.Id, Request(0)));
        var sameRequest = Request(1);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Retry(client, message.Id, sameRequest)));
        Assert.All(results, result => Assert.Equal("Solicitado", result));
        Assert.Equal("NoElegible", await Retry(client, message.Id, Request(1)));
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>(Url))!);
        await using var verify = data.CreateDbContext();
        var pending = await verify.OutboxMessages.SingleAsync(m => m.Id == message.Id);
        Assert.Equal(OutboxEstado.Pendiente, pending.Estado);
        Assert.Equal(message.Payload, pending.Payload);
        Assert.Equal(1, pending.Intentos);
        var audits = await verify.Database.SqlQuery<Audit>($"""SELECT "Id","ActorId","MensajeId","Resultado","Motivo" FROM public."OutboxRetryRequests" WHERE "MensajeId"={message.Id} """).ToArrayAsync();
        Assert.Equal(4, audits.Length);
        var accepted = Assert.Single(audits, a => a.Resultado == "Solicitado");
        Assert.Equal(op.UsuarioId, accepted.ActorId);
        Assert.Equal(sameRequest.SolicitudId, accepted.Id);
        Assert.Equal(sameRequest.Motivo, accepted.Motivo);
        var competingMessage = OutboxMessage.ForShipment(message.OperadorId!.Value, shipment, "GenerarSeguimiento", OutboxDestino.RabbitMq, message.Payload);
        verify.OutboxMessages.Add(competingMessage); await verify.SaveChangesAsync();
        await Fail(data, competingMessage.Id);
        var competingRequests = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => Retry(client, competingMessage.Id, Request(1))));
        Assert.Single(competingRequests, result => result == "Solicitado");
        Assert.Equal(3, competingRequests.Count(result => result == "NoElegible"));
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlRawAsync("DELETE FROM public.\"OutboxRetryRequests\""));
        Assert.Equal("55000", immutable.SqlState);
        var permission = await verify.Permisos.SingleAsync(p => p.Codigo == "configuracion.gestionar");
        verify.RolesPermisos.Remove(await verify.RolesPermisos.SingleAsync(r => r.RolId == op.RolId && r.PermisoId == permission.Id));
        await verify.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Url}/{message.Id}/reintentos", Request(1))).StatusCode);
        // Direct SQL through the API role is subject to the same permission check.
        await using var restricted = runtime.CreateDbContext();
        await using var tx = await restricted.Database.BeginTransactionAsync();
        await restricted.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('nexoruta.actor_kind','operador',true),set_config('nexoruta.org_id',{op.OperadorId.ToString()},true),set_config('nexoruta.access_id',{op.Id.ToString()},true),set_config('nexoruta.user_id',{op.UsuarioId.ToString()},true)");
        var error = await Assert.ThrowsAsync<PostgresException>(() => restricted.Database.SqlQuery<string>($"""SELECT nexoruta_app.request_outbox_retry({Guid.NewGuid()},{message.Id},1,{"x"}) AS "Value" """).SingleAsync());
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task RequestedRetryIsRecoveredByNormalPublisherAndRealRabbitConsumerOnlyOnce()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        await using var db = data.CreateDbContext();
        var commerce = await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null);
        var op = await db.AccesosUsuario.SingleAsync(a => a.OperadorId != null);
        var shipment = await CreateShipment(data, commerce.Id);
        var message = await db.OutboxMessages.SingleAsync(m => m.EnvioId == shipment && m.Tipo == "GenerarSeguimiento");
        await Fail(data, message.Id);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = api.CreateHttpClient();
        await client.SetBearerForAccessAsync(data, op.Id);
        Assert.Equal("Solicitado", await Retry(client, message.Id, Request(1)));
        await using var broker = new RabbitMqBuilder("rabbitmq:4-management").Build();
        await broker.StartAsync();
        var workerConnection = await WorkerConnection(data);
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["RabbitMq:ConnectionString"] = broker.GetConnectionString(),
            ["Seguimiento:EncryptionKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        });
        var uri = new Uri(broker.GetConnectionString());
        var credentials = uri.UserInfo.Split(':');
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["RabbitMq:Host"] = uri.Host, ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:Username"] = Uri.UnescapeDataString(credentials[0]), ["RabbitMq:Password"] = Uri.UnescapeDataString(credentials[1])
        });
        builder.Services.AddDbContext<NexoRutaDbContext>(o => o.UseNexoRutaPostgres(workerConnection));
        builder.Services.AddScoped<EfOutboxRepository>();
        builder.Services.AddSingleton<RabbitMqOutboxPublisher>();
        builder.Services.AddSingleton<IOutboxMessageHandler, RabbitTrackingOutboxHandler>();
        builder.Services.AddSingleton<IOutboxMessageHandler, RecipientStateOutboxHandler>();
        builder.Services.AddSingleton<OutboxHandlerRegistry>();
        builder.Services.AddScoped<IConsultaEstadoEnvio, EfConsultaEstadoEnvio>();
        builder.Services.AddScoped<IEnlacesSeguimientoRepository, EfEnlacesSeguimientoRepository>();
        builder.Services.AddScoped<CrearEnlaceSeguimientoUseCase>();
        builder.Services.AddSingleton<CifradorTokenSeguimiento>();
        builder.Services.AddScoped<ProcesarGeneracionSeguimiento>();
        builder.Services.AddHostedService<NexoRuta.Worker.Worker>();
        var acknowledgements = new Acknowledgements();
        builder.Services.AddSingleton<ILogger<GenerarSeguimientoConsumer>>(acknowledgements);
        builder.Services.AddHostedService<GenerarSeguimientoConsumer>();
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await WaitFor(async () => await db.ProcessedMessages.AnyAsync(p => p.MessageId == message.Id)
                && await db.OutboxMessages.AnyAsync(m => m.Id == message.Id && m.Estado == OutboxEstado.Publicado) && acknowledgements.Count >= 1);
            Assert.Equal("NoElegible", await Retry(client, message.Id, Request(2)));
            // Simulate a lost publisher confirmation: normal republish preserves MessageId.
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."OutboxMessages" SET "Estado"='Error',"ProximoIntentoEnUtc"=clock_timestamp()+interval '1 hour' WHERE "Id"={message.Id} """);
            Assert.DoesNotContain((await client.GetFromJsonAsync<JsonElement[]>(Url))!, row => row.GetProperty("id").GetGuid() == message.Id);
            Assert.Equal("NoElegible", await Retry(client, message.Id, Request(2)));
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id);
            await host.Services.GetRequiredService<RabbitMqOutboxPublisher>().PublishTrackingAsync(OutboxClaim.From(row), CancellationToken.None);
            await WaitFor(() => Task.FromResult(acknowledgements.Count >= 2));
            Assert.Equal(1, await db.ProcessedMessages.CountAsync(p => p.MessageId == message.Id));
            Assert.Equal(1, await db.EntregasSeguimiento.CountAsync(e => e.EnvioId == shipment));
        }
        finally { await host.StopAsync(); }
    }

    private static async Task<Guid> CreateShipment(IntegrationDatabase data, Guid commerce)
    {
        using var api = new IntegrationApiFactory(data, settings: RlsRestrictedHttpFlowTests.ApiSettings()); using var client = api.CreateHttpClient();
        await client.SetBearerForAccessAsync(data, commerce);
        await using var db = data.CreateDbContext(); var op = await db.Operadores.Select(o => o.Id).SingleAsync();
        using var response = await client.PostAsJsonAsync("/api/envios", new { OperadorId=op,DestinatarioNombre="Synthetic",DestinatarioEmail="retry@example.test",Direccion="Synthetic 123",Servicio=ServiciosEnvioTestData.Declarar(op),Bultos=new[] { new {Codigo="RETRY",PesoGramos=1000,LargoCentimetros=10,AnchoCentimetros=10,AltoCentimetros=10} } });
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task Fail(IntegrationDatabase data, Guid id)
    {
        await using var db = data.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."OutboxMessages" SET "Estado"='Error',"Intentos"=1,"UltimoError"='secret-do-not-expose',"ProximoIntentoEnUtc"=clock_timestamp()+interval '1 hour' WHERE "Id"={id} """);
    }
    private static RetryRequest Request(int attempts) => new(Guid.NewGuid(), attempts, "Causa corregida en entorno sintético");
    private static async Task<string> Retry(HttpClient client, Guid id, RetryRequest request)
    {
        using var response = await client.PostAsJsonAsync($"{Url}/{id}/reintentos", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resultado").GetString()!;
    }
    private static async Task<string> WorkerConnection(IntegrationDatabase data)
    {
        var role=$"retry_worker_{Guid.NewGuid():N}";
        var password=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var owner=new NpgsqlConnection(data.ConnectionString); await owner.OpenAsync();
        await using var create=new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT nexoruta_worker TO {role}",owner);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(data.ConnectionString){Username=role,Password=password,Pooling=false}.ConnectionString;
    }
    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while(!await condition()) await Task.Delay(100,timeout.Token);
    }
    private sealed class Acknowledgements : ILogger<GenerarSeguimientoConsumer>
    {
        private int count; public int Count => Volatile.Read(ref count);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (id.Id == 1001) Interlocked.Increment(ref count); }
    }
    private sealed record RetryRequest(Guid SolicitudId,int Intentos,string Motivo);
    private sealed class Audit
    {
        public Guid Id {get;set;} public Guid ActorId {get;set;} public Guid MensajeId {get;set;}
        public string Resultado {get;set;}=""; public string Motivo {get;set;}="";
    }
}
