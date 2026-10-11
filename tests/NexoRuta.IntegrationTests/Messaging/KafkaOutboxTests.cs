using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker;
using NexoRuta.Worker.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace NexoRuta.IntegrationTests.Messaging;

public sealed class KafkaOutboxTests(PostgresFixture postgres, KafkaFixture kafka)
    : IClassFixture<PostgresFixture>, IClassFixture<KafkaFixture>
{
    [Fact]
    public async Task PublicacionConfirmada_RespetaContratoYCompletaOutbox()
    {
        var database = await postgres.CreateDatabaseAsync();
        var evento = await CreateEventAsync(database);
        Assert.Equal(OutboxEstado.Pendiente, evento.Estado);
        using var host = CreateHost(database);
        await host.StartAsync();
        try
        {
            await WaitForStateAsync(database, evento.Id, OutboxEstado.Publicado);
            var recibido = Assert.Single(await ReceiveAsync(evento.Id));
            AssertContract(evento, recibido);
            var publicado = await ReadAsync(database, evento.Id);
            Assert.NotNull(publicado.PublicadoEnUtc);
            Assert.Null(publicado.ProcesadorId);
            Assert.Null(publicado.ReservaHastaUtc);
            Assert.Null(publicado.UltimoError);
            Assert.Equal(1, publicado.Intentos);
            await using var db = database.CreateDbContext();
            Assert.Equal(OutboxEstado.Pendiente, (await db.OutboxMessages.AsNoTracking()
                .SingleAsync(x => x.Destino == OutboxDestino.RabbitMq)).Estado);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task BrokerInaccesible_ConservaEventoYRecuperaDespuesDelReintento()
    {
        var database = await postgres.CreateDatabaseAsync();
        var evento = await CreateEventAsync(database);
        await kafka.Container.PauseAsync();
        var paused = true;
        using var host = CreateHost(database);
        await host.StartAsync();
        try
        {
            await WaitForStateAsync(database, evento.Id, OutboxEstado.Error);
            var failed = await ReadAsync(database, evento.Id);
            Assert.Null(failed.PublicadoEnUtc);
            Assert.Equal(evento.Payload, failed.Payload);
            Assert.Equal(1, failed.Intentos);
            Assert.StartsWith("Kafka:", failed.UltimoError);
            Assert.True(failed.ProximoIntentoEnUtc > DateTimeOffset.UtcNow);
            await kafka.Container.UnpauseAsync();
            paused = false;
            // Advance only this owned test record's retry deadline; production remains 30 s.
            await using (var db = database.CreateDbContext())
                await db.OutboxMessages.Where(x => x.Id == evento.Id).ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.ProximoIntentoEnUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
            await WaitForStateAsync(database, evento.Id, OutboxEstado.Publicado);
            AssertContract(evento, Assert.Single(await ReceiveAsync(evento.Id)));
            Assert.True((await ReadAsync(database, evento.Id)).Intentos >= 2);
        }
        finally
        {
            if (paused) await kafka.Container.UnpauseAsync();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ConfirmacionSinActualizacionSql_ReinicioRepublicaConEventIdEstable()
    {
        var database = await postgres.CreateDatabaseAsync();
        var evento = await CreateEventAsync(database);
        await using (var db = database.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_kafka_completion() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Estado" = 'Publicado' AND NEW."Destino" = 'Kafka' THEN
                        RAISE EXCEPTION 'Simulated SQL completion failure';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER reject_kafka_completion BEFORE UPDATE ON "OutboxMessages"
                FOR EACH ROW EXECUTE FUNCTION reject_kafka_completion();
                """);
        using (var first = CreateHost(database))
        {
            await first.StartAsync();
            try { AssertContract(evento, Assert.Single(await ReceiveAsync(evento.Id))); }
            finally { await first.StopAsync(); }
        }
        var inconcluso = await ReadAsync(database, evento.Id);
        Assert.Equal(OutboxEstado.Procesando, inconcluso.Estado);
        Assert.Null(inconcluso.PublicadoEnUtc);
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER reject_kafka_completion ON "OutboxMessages";
                DROP FUNCTION reject_kafka_completion();
                """);
            // Simulate expiration of the crashed owner's lease in the isolated database.
            await db.OutboxMessages.Where(x => x.Id == evento.Id).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ReservaHastaUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        using var second = CreateHost(database);
        await second.StartAsync();
        try
        {
            await WaitForStateAsync(database, evento.Id, OutboxEstado.Publicado);
            var duplicates = await ReceiveAsync(evento.Id, 2);
            Assert.Equal(2, duplicates.Count);
            foreach (var record in duplicates) AssertContract(evento, record);
            Assert.Equal(duplicates[0].Partition, duplicates[1].Partition);
            Assert.NotEqual(duplicates[0].Offset, duplicates[1].Offset);
            Assert.Equal(2, (await ReadAsync(database, evento.Id)).Intentos);
        }
        finally { await second.StopAsync(); }
    }

    [Theory]
    [InlineData(OutboxDestino.Kafka)]
    [InlineData(OutboxDestino.RabbitMq)]
    public async Task CaidaDeUnBroker_NoImpidePublicarEnElOtro(OutboxDestino failedTransport)
    {
        var database = await postgres.CreateDatabaseAsync();
        var evento = await CreateEventAsync(database);
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();
        await rabbit.StartAsync();
        var uri = new Uri(rabbit.GetConnectionString());
        var credentials = uri.UserInfo.Split(':', 2);
        var rabbitSettings = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = uri.Host, ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:Username"] = Uri.UnescapeDataString(credentials[0]),
            ["RabbitMq:Password"] = Uri.UnescapeDataString(credentials[1])
        };
        if (failedTransport == OutboxDestino.Kafka) await kafka.Container.PauseAsync();
        else await rabbit.StopAsync();
        using var kafkaHost = CreateHost(database);
        using var rabbitHost = CreateHost(database, rabbitSettings);
        try
        {
            await kafkaHost.StartAsync();
            await rabbitHost.StartAsync();
            await using var db = database.CreateDbContext();
            var trabajo = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Destino == OutboxDestino.RabbitMq);
            var healthy = failedTransport == OutboxDestino.Kafka ? trabajo : evento;
            var failed = failedTransport == OutboxDestino.Kafka ? evento : trabajo;
            await WaitForStateAsync(database, healthy.Id, OutboxEstado.Publicado);
            await WaitForStateAsync(database, failed.Id, OutboxEstado.Error);
            Assert.Null((await ReadAsync(database, failed.Id)).PublicadoEnUtc);
            if (failedTransport == OutboxDestino.RabbitMq)
                AssertContract(evento, Assert.Single(await ReceiveAsync(evento.Id)));
            else
            {
                await using var connection = await new ConnectionFactory { Uri = uri }.CreateConnectionAsync();
                await using var channel = await connection.CreateChannelAsync();
                var delivered = await channel.BasicGetAsync(RabbitMqSeguimientoTopology.ColaPrincipal, false);
                Assert.NotNull(delivered);
                Assert.Equal(trabajo.Id.ToString("D"), delivered.BasicProperties.MessageId);
                Assert.Equal(trabajo.Payload, Encoding.UTF8.GetString(delivered.Body.Span));
                await channel.BasicAckAsync(delivered.DeliveryTag, false);
            }
        }
        finally
        {
            if (failedTransport == OutboxDestino.Kafka) await kafka.Container.UnpauseAsync();
            await kafkaHost.StopAsync();
            await rabbitHost.StopAsync();
        }
    }

    private IHost CreateHost(IntegrationDatabase database, Dictionary<string, string?>? rabbitSettings = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers, ["Kafka:MessageTimeoutMs"] = "5000"
        });
        builder.Services.AddDbContext<NexoRutaDbContext>(options => options.UseNpgsql(database.ConnectionString));
        builder.Services.AddScoped<EfOutboxRepository>();
        if (rabbitSettings is null)
        {
            builder.Services.AddSingleton<KafkaEnvioCreadoPublisher>();
            builder.Services.AddHostedService<KafkaOutboxWorker>();
        }
        else
        {
            builder.Configuration.AddInMemoryCollection(rabbitSettings);
            builder.Services.AddSingleton<RabbitMqSeguimientoPublisher>();
            builder.Services.AddHostedService<NexoRuta.Worker.Worker>();
        }
        return builder.Build();
    }

    private static async Task<OutboxMessage> CreateEventAsync(IntegrationDatabase database)
    {
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var operadorId = await db.Operadores.Select(x => x.Id).SingleAsync();
        var accesoId = await db.AccesosUsuario.Where(x => x.ComercioId != null).Select(x => x.Id).SingleAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/envios")
        {
            Content = JsonContent.Create(new
            {
                operadorId, destinatarioNombre = "Kafka test recipient", direccion = "Test address",
                codigoBulto = $"Kafka-{Guid.NewGuid():N}", pesoGramos = 1200m,
                largoCentimetros = 25m, anchoCentimetros = 20m, altoCentimetros = 15m
            })
        };
        request.Headers.Add("X-NexoRuta-Acceso", accesoId.ToString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Destino == OutboxDestino.Kafka);
    }

    private static async Task<OutboxMessage> ReadAsync(IntegrationDatabase database, Guid id)
    {
        await using var db = database.CreateDbContext();
        return await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static async Task WaitForStateAsync(IntegrationDatabase database, Guid id, OutboxEstado expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while ((await ReadAsync(database, id)).Estado != expected)
            await Task.Delay(100, deadline.Token);
    }

    private Task<List<ConsumeResult<string, string>>> ReceiveAsync(Guid eventId, int count = 1) => Task.Run(() =>
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers, GroupId = $"integration-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false
        }).SetLogHandler((_, _) => { }).Build();
        consumer.Subscribe(KafkaEnvioCreadoPublisher.Topic);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = new List<ConsumeResult<string, string>>();
        try
        {
            while (result.Count < count)
            {
                var record = consumer.Consume(deadline.Token);
                if (Encoding.UTF8.GetString(record.Message.Headers.GetLastBytes("eventId")) == eventId.ToString("D"))
                    result.Add(record);
            }
            return result;
        }
        finally { consumer.Close(); }
    });

    private static void AssertContract(OutboxMessage evento, ConsumeResult<string, string> record)
    {
        Assert.Equal(KafkaEnvioCreadoPublisher.Topic, record.Topic);
        Assert.Equal(evento.EnvioId.ToString("D"), record.Message.Key);
        Assert.Equal(evento.Payload, record.Message.Value);
        var expected = new Dictionary<string, string>
        {
            ["eventId"] = evento.Id.ToString("D"), ["eventType"] = "EnvioCreado", ["eventVersion"] = "1",
            ["occurredAt"] = evento.CreadoEnUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["operadorId"] = evento.OperadorId.ToString("D"), ["contentType"] = "application/json"
        };
        Assert.Equal(expected.Count, record.Message.Headers.Count);
        foreach (var (key, value) in expected)
            Assert.Equal(value, Encoding.UTF8.GetString(record.Message.Headers.GetLastBytes(key)));
        using var json = JsonDocument.Parse(record.Message.Value);
        Assert.Equal(4, json.RootElement.EnumerateObject().Count());
        Assert.Equal(evento.EnvioId, json.RootElement.GetProperty("envioId").GetGuid());
        Assert.Equal(evento.OperadorId, json.RootElement.GetProperty("operadorId").GetGuid());
        Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("comercioId").GetGuid());
        Assert.Equal("Admitido", json.RootElement.GetProperty("estado").GetString());
    }
}
