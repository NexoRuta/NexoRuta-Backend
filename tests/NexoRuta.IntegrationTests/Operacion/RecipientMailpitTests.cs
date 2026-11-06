using System.Data.Common;
using Xunit.Abstractions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Correo;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Security;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguimiento;
using NexoRuta.Worker.Messaging;
using Testcontainers.RabbitMq;

namespace NexoRuta.IntegrationTests.Operacion;

[Collection(OperationalInfrastructureCollection.Name)]
public sealed class RecipientMailpitTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task PersistedNoticeUsesRealMailpitAndRecoversThroughRabbitRetryWithoutLogicalDuplicates()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var ct = deadline.Token;
        await using var mailpit = new ContainerBuilder("axllent/mailpit:v1.27.10")
            .WithPortBinding(1025, true).WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1025)).Build();
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();
        await Task.WhenAll(mailpit.StartAsync(ct), rabbit.StartAsync(ct));
        var database = await postgres.CreateDatabaseAsync();
        var uri = new Uri(rabbit.GetConnectionString()); var credentials = uri.UserInfo.Split(':', 2);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = uri.Host,
            ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:Username"] = Uri.UnescapeDataString(credentials[0]),
            ["RabbitMq:Password"] = Uri.UnescapeDataString(credentials[1]),
            ["Seguimiento:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Tracking:PublicPortalBaseUrl"] = "https://tracking.example.test"
        }).Build();
        var cipher = new CifradorTokenSeguimiento(config);
        Guid shipmentId, operatorId;
        await using (var seed = database.CreateDbContext())
        {
            var shipment = await SeguimientoTestData.CreateEnvioAsync(seed); shipmentId = shipment.Id; operatorId = shipment.OperadorId;
            seed.VersionesIdentidadVisualOperador.Add(new VersionIdentidadVisualOperador(operatorId, 1, "Marca sintética", "contacto@example.test", null, "#1D4ED8", null, null, null, shipment.CreadoPorUsuarioId, DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync(ct);
        }
        var job = await PublishedTrackingJobTestData.CreatePublishedTrackingJobAsync(database, operatorId, shipmentId);
        await using (var db = database.CreateDbContext())
            await new ProcesarGeneracionSeguimiento(db, new CrearEnlaceSeguimientoUseCase(new EfConsultaEstadoEnvio(db), new EfEnlacesSeguimientoRepository(db)), cipher).EjecutarAsync(job, operatorId, shipmentId, ct);
        OutboxMessage notice;
        await using (var db = database.CreateDbContext()) notice = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Tipo == OutboxMessageTypes.RecipientStateEmail, ct);
        await PublishedTrackingJobTestData.PublishPendingAsync(database, notice.Id);
        // First SMTP attempt uses a deliberately closed loopback port, never an external provider.
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var smtp = new ConfiguracionSmtp { Host = "127.0.0.1", Port = closedPort, FromAddress = "avisos@example.test", FromName = "NexoRuta prueba" };
        var sender = new ObservedSender(new SmtpCorreoSender(Options.Create(smtp)));
        await using var services = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton(cipher).AddSingleton<ICorreoSender>(sender)
            .AddDbContext<NexoRutaDbContext>(options => options.UseNexoRutaPostgres(database.ConnectionString).AddInterceptors(new WorkerRole()))
            .AddScoped<RecipientStateEmailProcessor>().BuildServiceProvider();
        using var consumer = new RecipientStateEmailConsumer(config, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<RecipientStateEmailConsumer>.Instance);
        await consumer.StartAsync(ct);
        await using var publisher = new RabbitMqOutboxPublisher(config);
        try
        {
            await publisher.PublishRecipientStateAsync(OutboxClaim.From(notice), ct);
            await sender.FirstFailure.Task.WaitAsync(ct);
            await using (var db = database.CreateDbContext())
                Assert.Null(await db.Database.SqlQuery<DateTimeOffset?>($"SELECT \"EnviadoEnUtc\" AS \"Value\" FROM public.\"AvisosDestinatarioEstado\" WHERE \"Id\"={notice.Id}").SingleAsync(ct));
            smtp.Port = mailpit.GetMappedPublicPort(1025);
            using var http = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}") };
            JsonElement messages;
            do { await Task.Delay(100, ct); messages = await http.GetFromJsonAsync<JsonElement>("/api/v1/messages", ct); } while (messages.GetProperty("total").GetInt32() != 1);
            var id = messages.GetProperty("messages")[0].GetProperty("ID").GetString();
            var received = await http.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}", ct);
            Assert.Contains("Marca sintética", WebUtility.HtmlDecode(received.GetProperty("HTML").GetString()));
            Assert.Contains("https://tracking.example.test/seguimiento/", received.GetProperty("HTML").GetString());
            Assert.DoesNotContain(shipmentId.ToString(), received.GetProperty("HTML").GetString());
            do
            {
                await Task.Delay(50, ct); await using var db = database.CreateDbContext();
                if (await db.Database.SqlQuery<DateTimeOffset?>($"SELECT \"EnviadoEnUtc\" AS \"Value\" FROM public.\"AvisosDestinatarioEstado\" WHERE \"Id\"={notice.Id}").SingleAsync(ct) is not null) break;
            } while (true);
            await publisher.PublishRecipientStateAsync(OutboxClaim.From(notice), ct);
            await Task.Delay(1000, ct);
            Assert.Equal(1, (await http.GetFromJsonAsync<JsonElement>("/api/v1/messages", ct)).GetProperty("total").GetInt32());
            Assert.True(sender.Attempts >= 2);
            output.WriteLine(JsonSerializer.Serialize(new { mailpitMessageId = id, received = 1, smtpAttempts = sender.Attempts, controlledSmtpFailureRecovered = true, duplicateMessageDidNotResend = true, operatorBrandAndOpaqueLinkVerified = true }));
        }
        finally { await consumer.StopAsync(CancellationToken.None); }
    }

    private sealed class WorkerRole : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        { using var command = connection.CreateCommand(); command.CommandText = "SET ROLE nexoruta_worker"; await command.ExecuteNonQueryAsync(cancellationToken); }
    }
    private sealed class ObservedSender(ICorreoSender inner) : ICorreoSender
    {
        public TaskCompletionSource FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts;
        public Task EnviarAsync(string to, string subject, string body, CancellationToken ct) => inner.EnviarAsync(to, subject, body, ct);
        public async Task EnviarHtmlAsync(string to, string subject, string text, string html, byte[]? image, string? imageType, CancellationToken ct)
        { Interlocked.Increment(ref Attempts); try { await inner.EnviarHtmlAsync(to, subject, text, html, image, imageType, ct); } catch { FirstFailure.TrySetResult(); throw; } }
    }
}
