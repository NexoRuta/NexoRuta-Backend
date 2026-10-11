using Confluent.Kafka;
using Confluent.Kafka.Admin;
using NexoRuta.Worker.Messaging;
using Testcontainers.Kafka;

namespace NexoRuta.IntegrationTests.Fixtures;

public sealed class KafkaFixture : IAsyncLifetime
{
    public KafkaContainer Container { get; } = new KafkaBuilder("confluentinc/cp-kafka:7.9.0").WithKRaft().Build();
    public string BootstrapServers => Container.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        try
        {
            await Container.StartAsync();
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers })
                .SetLogHandler((_, _) => { }).Build();
            await admin.CreateTopicsAsync([new TopicSpecification
            {
                Name = KafkaEnvioCreadoPublisher.Topic, NumPartitions = 3, ReplicationFactor = 1
            }]);
        }
        catch
        {
            await Container.DisposeAsync();
            throw;
        }
    }

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
