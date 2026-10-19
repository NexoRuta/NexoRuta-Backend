using RabbitMQ.Client;

namespace NexoRuta.Worker.Messaging;

public static class RabbitMqWorkerConnection
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public static ConnectionFactory CreateFactory(IConfiguration configuration)
    {
        var username = configuration[WorkerConfigurationKeys.RabbitUsername];
        var password = configuration[WorkerConfigurationKeys.RabbitPassword];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("RabbitMQ technical credentials are required.");
        return new ConnectionFactory
        {
            HostName = configuration[WorkerConfigurationKeys.RabbitHost] ?? "127.0.0.1",
            Port = checked((ushort)configuration.GetValue(WorkerConfigurationKeys.RabbitPort, 5672)),
            UserName = username,
            Password = password,
            AutomaticRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };
    }

    public static async Task<IConnection> ConnectWithRetryAsync(
        ConnectionFactory factory, ILogger logger, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { return await factory.CreateConnectionAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning("RabbitMQ is unavailable during consumer startup. ExceptionType: {ExceptionType}; retrying.",
                    error.GetType().Name);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }
}
