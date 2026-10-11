
using RabbitMQ.Client;

namespace NexoRuta.Worker.Messaging;

public static class RabbitMqSeguimientoTopology
{
    public const string ColaPrincipal =
        RabbitMqSeguimientoPublisher.NombreCola;

    public const string ColaReintento =
        "nexoruta.seguimiento.reintento";

    public const string ColaFallidos =
        "nexoruta.seguimiento.fallidos";

    public static async Task DeclararAsync(
        IChannel channel,
        CancellationToken cancellationToken = default)
    {
        // Cola existente: no cambiar sus argumentos.
        await channel.QueueDeclareAsync(
            queue: ColaPrincipal,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        // Cola que retiene los mensajes durante 30 segundos.
        await channel.QueueDeclareAsync(
            queue: ColaReintento,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = 30_000,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = ColaPrincipal
            },
            cancellationToken: cancellationToken);

        // Cola donde conservar errores permanentes.
        await channel.QueueDeclareAsync(
            queue: ColaFallidos,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
    }
}
