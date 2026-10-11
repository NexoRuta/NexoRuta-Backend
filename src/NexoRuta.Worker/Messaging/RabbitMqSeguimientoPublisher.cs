
using System.Text;
using Microsoft.Extensions.Configuration;
using NexoRuta.Infrastructure.Outbox;
using RabbitMQ.Client;

namespace NexoRuta.Worker.Messaging;

public sealed class RabbitMqSeguimientoPublisher(
    IConfiguration configuration)
{
    public const string NombreCola =
        "nexoruta.seguimiento.generar";

    public async Task PublicarAsync(
        OutboxMessage mensaje,
        CancellationToken cancellationToken = default)
    {
        if (mensaje.Destino != OutboxDestino.RabbitMq ||
            mensaje.Tipo != "GenerarSeguimiento")
        {
            throw new ArgumentException(
                "El mensaje no corresponde a la cola de seguimiento.",
                nameof(mensaje));
        }

        var usuario = configuration["RabbitMq:Username"];
        var password = configuration["RabbitMq:Password"];

        if (string.IsNullOrWhiteSpace(usuario) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Faltan las credenciales de RabbitMQ.");
        }

        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMq:Host"] ?? "127.0.0.1",
            Port = int.TryParse(
                configuration["RabbitMq:Port"], out var puerto)
                    ? puerto
                    : 5672,
            UserName = usuario,
            Password = password
        };

        await using var connection =
            await factory.CreateConnectionAsync(cancellationToken);

        var opciones = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        await using var channel =
            await connection.CreateChannelAsync(
                opciones, cancellationToken);

        await channel.QueueDeclareAsync(
            queue: NombreCola,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var propiedades = new BasicProperties
        {
            MessageId = mensaje.Id.ToString("D"),
            ContentType = "application/json",
            Type = mensaje.Tipo,
            Persistent = true
        };

        var contenido = Encoding.UTF8.GetBytes(mensaje.Payload);

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: NombreCola,
            mandatory: true,
            basicProperties: propiedades,
            body: contenido,
            cancellationToken: cancellationToken);
    }
}
