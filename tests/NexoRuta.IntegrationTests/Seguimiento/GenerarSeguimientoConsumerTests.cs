
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexoRuta.Worker.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using System.Text.Json;
namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class GenerarSeguimientoConsumerTests
{
    [Fact]
    public async Task MensajeConJsonInvalido_SeEnviaAFallidos()
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

        var messageId = Guid.NewGuid().ToString("D");

        // JSON intencionalmente incorrecto.
        var contenido = Encoding.UTF8.GetBytes(
            "{ esto no es JSON válido");

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
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task ErrorDeProcesamiento_RedirigeSegunIntentos(
        int intentosPrevios,
        bool debeIrAFallidos)
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

        // Registramos el consumidor, pero NO el procesador.
        // Esto provoca un fallo de procesamiento controlado.
        builder.Services
            .AddHostedService<GenerarSeguimientoConsumer>();

        using var host = builder.Build();

        await host.StartAsync();

        try
        {
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
                    Persistent = true,
                    Headers = new Dictionary<string, object?>
                    {
                        ["x-nexoruta-retry-count"] = intentosPrevios
                    }
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
                : intentosPrevios + 1;

            Assert.Equal(
                reintentosEsperados,
                Convert.ToInt32(valorReintentos));

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

}
