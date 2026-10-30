
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexoRuta.Worker.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using System.Text.Json;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class GenerarSeguimientoConsumerTests
{
    [Theory]
    [InlineData("{ invalid JSON", false)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("{\"envioId\":\"bad-guid\"}", false)]
    [InlineData("{}", true)]
    public async Task MensajeConContratoInvalido_SeEnviaAFallidos(string payload, bool idInvalido)
    {
        // ARRANGE: RabbitMQ completamente aislado.
        await using var rabbitMq =
            new RabbitMqBuilder("rabbitmq:4-management")
                .Build();

        await rabbitMq.StartAsync();

        var uri = new Uri(rabbitMq.GetConnectionString());
        var credenciales = uri.UserInfo.Split(':', 2);

        Assert.Equal(2, credenciales.Length);

        var factory = new ConnectionFactory
        {
            Uri = uri
        };

        await using var conexion =
            await factory.CreateConnectionAsync();

        var opciones = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        await using var canal =
            await conexion.CreateChannelAsync(opciones);

        await RabbitMqSeguimientoTopology.DeclararAsync(canal);

        var messageId = idInvalido ? "invalid-id" : Guid.NewGuid().ToString("D");

        // JSON intencionalmente incorrecto.
        var contenido = Encoding.UTF8.GetBytes(payload);

        await canal.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: RabbitMqSeguimientoTopology.ColaPrincipal,
            mandatory: true,
            basicProperties: new BasicProperties
            {
                MessageId = messageId,
                Type = "GenerarSeguimiento",
                ContentType = "application/json",
                Persistent = true
            },
            body: contenido);

        // Arrancar exclusivamente el consumidor real.
        // No registramos el publicador Outbox ni usamos
        // el PostgreSQL compartido.
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = uri.Host,
                ["RabbitMq:Port"] = uri.Port.ToString(),
                ["RabbitMq:Username"] =
                    Uri.UnescapeDataString(credenciales[0]),
                ["RabbitMq:Password"] =
                    Uri.UnescapeDataString(credenciales[1])
            });

        builder.Services
            .AddHostedService<GenerarSeguimientoConsumer>();

        using var host = builder.Build();

        await host.StartAsync();

        try
        {
            // ACT: esperar a que el consumidor desvíe
            // el mensaje a la cola de fallidos.
            BasicGetResult? recibido = null;

            for (var intento = 0; intento < 80; intento++)
            {
                recibido = await canal.BasicGetAsync(
                    queue:
                        RabbitMqSeguimientoTopology.ColaFallidos,
                    autoAck: false);

                if (recibido is not null)
                {
                    break;
                }

                await Task.Delay(250);
            }

            // ASSERT: mismo mensaje, enviado a fallidos.
            Assert.NotNull(recibido);

            Assert.Equal(
                messageId,
                recibido.BasicProperties.MessageId);

            Assert.Equal(
                contenido,
                recibido.Body.ToArray());

            Assert.NotNull(recibido.BasicProperties.Headers);

            Assert.True(
                recibido.BasicProperties.Headers.TryGetValue(
                    "x-nexoruta-retry-count",
                    out var valorReintentos));

            Assert.Equal(
                0,
                Convert.ToInt32(valorReintentos));

            Assert.True(
                recibido.BasicProperties.Headers.TryGetValue(
                    "x-nexoruta-error-kind",
                    out var tipoError));

            var bytesTipoError = Assert.IsType<byte[]>(tipoError);

            Assert.Equal(
                "invalid",
                Encoding.UTF8.GetString(bytesTipoError));
            // Confirmar el mensaje recibido por el test.
            await canal.BasicAckAsync(
                deliveryTag: recibido.DeliveryTag,
                multiple: false);
        }
        finally
        {
            // Cerrar el consumidor incluso si hay errores.
            await host.StopAsync();
        }

        // Tras cerrar el consumidor, el original no debe
        // reaparecer en la cola principal.
        var principal = await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaPrincipal);

        var fallidos = await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaFallidos);

        Assert.Equal(0u, principal.MessageCount);
        Assert.Equal(0u, fallidos.MessageCount);
    }

    [Theory]
    [InlineData(0, false, "processing", 0)]
    [InlineData(2, true, "processing", 2)]
    [InlineData(2, false, "timeout", 2)]
    [InlineData(int.MaxValue, false, "timeout", 3)]
    [InlineData(4, false, "processing", 0)]
    [InlineData(4, true, "processing", 2)]
    [InlineData(4, false, "timeout", 2)]
    [InlineData(2, false, "postgres", 2)]
    public async Task ErrorDeProcesamiento_RedirigeSegunIntentos(
        int intentosPrevios,
        bool debeIrAFallidos,
        string causa,
        int? fallosPrevios)
    {
        await using var rabbitMq =
            new RabbitMqBuilder("rabbitmq:4-management")
                .Build();

        await rabbitMq.StartAsync();

        var uri = new Uri(rabbitMq.GetConnectionString());
        var credenciales = uri.UserInfo.Split(':', 2);

        Assert.Equal(2, credenciales.Length);

        var factory = new ConnectionFactory { Uri = uri };

        await using var conexion =
            await factory.CreateConnectionAsync();

        var opciones = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        await using var canal =
            await conexion.CreateChannelAsync(opciones);

        await RabbitMqSeguimientoTopology.DeclararAsync(canal);

        var messageId = Guid.NewGuid().ToString("D");

        // JSON válido con identificadores válidos.
        var contenido = JsonSerializer.SerializeToUtf8Bytes(new
        {
            envioId = Guid.NewGuid(),
            operadorId = Guid.NewGuid()
        });

        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = uri.Host,
                ["RabbitMq:Port"] = uri.Port.ToString(),
                ["RabbitMq:Username"] =
                    Uri.UnescapeDataString(credenciales[0]),
                ["RabbitMq:Password"] =
                    Uri.UnescapeDataString(credenciales[1])
            });

        var transitorio = causa != "processing";
        // Inject the concrete shape returned by Npgsql's default EF execution strategy.
        if (transitorio)
            builder.Services.AddScoped<ProcesarGeneracionSeguimiento>(_ =>
                throw (causa == "postgres"
                    ? new InvalidOperationException("EF strategy", new DbUpdateException("EF",
                        new PostgresException("Serialization failure", "ERROR", "ERROR", "40001")))
                    : new TimeoutException("Simulated infrastructure timeout.")));

        // Without a processor, DI throws a non-transient processing error.
        builder.Services
            .AddHostedService<GenerarSeguimientoConsumer>();

        using var host = builder.Build();

        await host.StartAsync();

        try
        {
            var headers = new Dictionary<string, object?>
            {
                ["x-nexoruta-retry-count"] = intentosPrevios,
                ["x-test-preserved"] = "unchanged"
            };
            if (fallosPrevios.HasValue)
                headers["x-nexoruta-processing-failure-count"] = fallosPrevios.Value;

            await canal.BasicPublishAsync(
                exchange: string.Empty,
                routingKey:
                    RabbitMqSeguimientoTopology.ColaPrincipal,
                mandatory: true,
                basicProperties: new BasicProperties
                {
                    MessageId = messageId,
                    Type = "GenerarSeguimiento",
                    ContentType = "application/json",
                    CorrelationId = messageId,
                    Persistent = true,
                    Headers = headers
                },
                body: contenido);

            BasicGetResult? recibido = null;
            var colaDestino = debeIrAFallidos
                ? RabbitMqSeguimientoTopology.ColaFallidos
                : RabbitMqSeguimientoTopology.ColaReintento;
            // Esperar hasta 10 segundos.
            // La cola de reintento tiene TTL de 30 segundos.
            for (var intento = 0; intento < 40; intento++)
            {
                recibido = await canal.BasicGetAsync(
                    colaDestino,
                    autoAck: false);

                if (recibido is not null)
                    break;

                await Task.Delay(250);
            }

            Assert.NotNull(recibido);

            Assert.Equal(
                messageId,
                recibido.BasicProperties.MessageId);

            Assert.Equal(
                contenido,
                recibido.Body.ToArray());

            // Primer fallo: contador de intentos = 1.
            Assert.NotNull(recibido.BasicProperties.Headers);

            Assert.True(
                recibido.BasicProperties.Headers.TryGetValue(
                    "x-nexoruta-retry-count",
                    out var valorReintentos));

            var reintentosEsperados = debeIrAFallidos
                ? intentosPrevios
                : intentosPrevios == int.MaxValue ? int.MaxValue : intentosPrevios + 1;

            Assert.Equal(
                reintentosEsperados,
                Convert.ToInt32(valorReintentos));

            Assert.Equal(transitorio ? "transient" : "processing", Encoding.UTF8.GetString(
                Assert.IsType<byte[]>(recibido.BasicProperties.Headers["x-nexoruta-error-kind"])));
            var presupuestoPrevio = fallosPrevios.GetValueOrDefault();
            Assert.Equal(transitorio ? presupuestoPrevio : Math.Min(presupuestoPrevio, 2) + 1,
                Convert.ToInt32(recibido.BasicProperties.Headers["x-nexoruta-processing-failure-count"]));
            Assert.Equal(messageId, recibido.BasicProperties.CorrelationId);
            Assert.True(recibido.BasicProperties.Persistent);
            Assert.Equal("unchanged", Encoding.UTF8.GetString(
                Assert.IsType<byte[]>(recibido.BasicProperties.Headers["x-test-preserved"])));

            // Confirmar la copia de reintento en esta prueba
            // para que no vuelva a la cola principal por TTL.
            await canal.BasicAckAsync(
                recibido.DeliveryTag,
                multiple: false);
        }
        finally
        {
            await host.StopAsync();
        }

        // Tras detener el consumidor, el mensaje original
        // no debe reaparecer en la cola principal.
        var principal = await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaPrincipal);

        var colaNoEsperada = debeIrAFallidos
            ? RabbitMqSeguimientoTopology.ColaReintento
            : RabbitMqSeguimientoTopology.ColaFallidos;

        var noEsperada = await canal.QueueDeclarePassiveAsync(
            colaNoEsperada);

        Assert.Equal(0u, principal.MessageCount);
        Assert.Equal(0u, noEsperada.MessageCount);
    }

    [Fact]
    public async Task RepublicacionSinDestinoConfirmado_NoHaceAckDelOriginal()
    {
        await using var rabbitMq = new RabbitMqBuilder("rabbitmq:4-management").Build();
        await rabbitMq.StartAsync();
        var uri = new Uri(rabbitMq.GetConnectionString());
        var credenciales = uri.UserInfo.Split(':', 2);
        await using var conexion = await new ConnectionFactory { Uri = uri }.CreateConnectionAsync();
        await using var canal = await conexion.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = uri.Host,
            ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:Username"] = Uri.UnescapeDataString(credenciales[0]),
            ["RabbitMq:Password"] = Uri.UnescapeDataString(credenciales[1])
        });
        builder.Services.AddHostedService<GenerarSeguimientoConsumer>();
        using var host = builder.Build();
        var detenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registro = host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() => detenido.TrySetResult());
        var contenido = JsonSerializer.SerializeToUtf8Bytes(new
        {
            envioId = Guid.NewGuid(), operadorId = Guid.NewGuid()
        });
        var messageId = Guid.NewGuid().ToString("D");

        await host.StartAsync();
        try
        {
            // Wait for the real consumer to declare topology and register itself.
            for (var i = 0; i < 200; i++)
            {
                await RabbitMqSeguimientoTopology.DeclararAsync(canal);
                if ((await canal.QueueDeclarePassiveAsync(RabbitMqSeguimientoTopology.ColaPrincipal)).ConsumerCount == 1)
                    break;
                await Task.Delay(100);
            }
            Assert.Equal(1u, (await canal.QueueDeclarePassiveAsync(
                RabbitMqSeguimientoTopology.ColaPrincipal)).ConsumerCount);

            // Remove ONLY the retry queue in this owned, temporary broker.
            await canal.QueueDeleteAsync(RabbitMqSeguimientoTopology.ColaReintento);
            await canal.BasicPublishAsync(string.Empty, RabbitMqSeguimientoTopology.ColaPrincipal, true,
                new BasicProperties { MessageId = messageId, Type = "GenerarSeguimiento", Persistent = true },
                contenido);
            await detenido.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await host.StopAsync();
        }

        // Channel close must release the original, rather than silently lose it.
        var original = await canal.BasicGetAsync(RabbitMqSeguimientoTopology.ColaPrincipal, false);
        Assert.NotNull(original);
        Assert.True(original.Redelivered);
        Assert.Equal(messageId, original.BasicProperties.MessageId);
        Assert.Equal(contenido, original.Body.ToArray());
        Assert.False(original.BasicProperties.Headers?.ContainsKey("x-nexoruta-retry-count") ?? false);
        Assert.False(original.BasicProperties.Headers?.ContainsKey("x-nexoruta-error-kind") ?? false);
        Assert.Equal(0u, (await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaFallidos)).MessageCount);
        await canal.BasicAckAsync(original.DeliveryTag, false);
    }

}
