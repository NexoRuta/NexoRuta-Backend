
using NexoRuta.Worker.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using System.Text;
namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class RabbitMqSeguimientoTopologyTests
{
    [Fact]
    public async Task DeclararTopologia_CreaLasTresColas()
    {
        // RabbitMQ temporal, independiente del compartido.
        await using var rabbitMq =
            new RabbitMqBuilder("rabbitmq:4-management")
                .Build();

        await rabbitMq.StartAsync();

        var factory = new ConnectionFactory
        {
            Uri = new Uri(rabbitMq.GetConnectionString())
        };

        await using var conexion =
            await factory.CreateConnectionAsync();

        await using var canal =
            await conexion.CreateChannelAsync();

        // Ejecutar nuestra topología real.
        await RabbitMqSeguimientoTopology.DeclararAsync(
            canal);

        // Verificar que RabbitMQ creó las tres colas.
        var principal = await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaPrincipal);

        var reintento = await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaReintento);

        var fallidos = await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaFallidos);

        // Inicialmente no debe haber mensajes.
        Assert.Equal(0u, principal.MessageCount);
        Assert.Equal(0u, reintento.MessageCount);
        Assert.Equal(0u, fallidos.MessageCount);
    }
    
[Fact]
public async Task MensajeExpiradoEnReintento_RegresaAColaPrincipal()
{
    // ARRANGE: broker RabbitMQ aislado.
    await using var rabbitMq =
        new RabbitMqBuilder("rabbitmq:4-management")
            .Build();

    await rabbitMq.StartAsync();

    var factory = new ConnectionFactory
    {
        Uri = new Uri(rabbitMq.GetConnectionString())
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

    var contenido = Encoding.UTF8.GetBytes(
        """{"evento":"prueba-reintento"}""");

    var propiedades = new BasicProperties
    {
        MessageId = messageId,
        Type = "GenerarSeguimiento",
        ContentType = "application/json",
        Persistent = true,

        // Expiración individual para acelerar el test.
        // La cola mantiene su TTL configurado de 30 s.
        Expiration = "1000"
    };

    // ACT: publicar en la cola de reintento.
    await canal.BasicPublishAsync(
        exchange: string.Empty,
        routingKey:
            RabbitMqSeguimientoTopology.ColaReintento,
        mandatory: true,
        basicProperties: propiedades,
        body: contenido);

    // Esperar a que RabbitMQ expire el mensaje y
    // lo redirija automáticamente a la cola principal.
    BasicGetResult? recibido = null;

    for (var intento = 0; intento < 60; intento++)
    {
        recibido = await canal.BasicGetAsync(
            queue:
                RabbitMqSeguimientoTopology.ColaPrincipal,
            autoAck: false);

        if (recibido is not null)
        {
            break;
        }

        await Task.Delay(250);
    }

    // ASSERT: mensaje recibido después del dead-letter.
    Assert.NotNull(recibido);

    Assert.Equal(
        messageId,
        recibido.BasicProperties.MessageId);

    Assert.Equal(
        "GenerarSeguimiento",
        recibido.BasicProperties.Type);

    Assert.Equal(
        contenido,
        recibido.Body.ToArray());

    // Confirmar solamente el mensaje de esta prueba.
    await canal.BasicAckAsync(
        deliveryTag: recibido.DeliveryTag,
        multiple: false);
}

}
