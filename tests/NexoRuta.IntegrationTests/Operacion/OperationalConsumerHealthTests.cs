using System.Collections.Concurrent;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Application.Operacion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker.Messaging;

namespace NexoRuta.IntegrationTests.Operacion;

[Collection(OperationalInfrastructureCollection.Name)]
public sealed class OperationalConsumerHealthTests(KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    [Fact]
    public async Task EmptyPartitionsReportHealthAndContinueAfterTheFirstRealEvent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        var topic = "empty-health-" + Guid.NewGuid().ToString("N");
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 2, ReplicationFactor = 1 }]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Operational:Topic"] = topic,
            ["Operational:Partitions"] = "2",
            ["Operational:health-probe:Group"] = "health-probe-" + Guid.NewGuid().ToString("N")
        }).Build();
        using var consumer = new HealthProbe(config);
        await consumer.StartAsync(ct);
        try
        {
            await consumer.EmptyHealth.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            Assert.Equal(2, consumer.Health.Count);
            Assert.All(consumer.Health.Values, sample => Assert.Equal(0, sample.Lag));
            var now = DateTimeOffset.UtcNow;
            var route = Guid.NewGuid();
            var fact = new EventoOperativo(1, Guid.NewGuid(), Guid.NewGuid(), null, "Posicion", route, null,
                route, 1, Guid.NewGuid(), now, now,
                JsonSerializer.SerializeToElement(new { latitud = -34d, longitud = -56d, precisionMetros = 10d }));
            fact.Validar();
            using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers, Acks = Acks.All }).Build();
            await producer.ProduceAsync(new TopicPartition(topic, 1), new Message<string, string>
            {
                Key = fact.OperadorId.ToString("D") + ":" + fact.EntidadId.ToString("D"),
                Value = JsonSerializer.Serialize(fact, OperationalConsumer.Json)
            }, ct);
            await consumer.HealthAfterEvent.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            Assert.Equal(1, consumer.Projected);
            Assert.All(consumer.Health.Values, sample => Assert.Equal((0L, true), sample));
            await consumer.StopAsync(ct);
            using var resumed = new HealthProbe(config);
            await resumed.StartAsync(ct);
            try
            {
                await resumed.EmptyHealth.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                Assert.Equal(0, resumed.Projected); // Same group resumes its committed end, without replay.
                Assert.Equal(2, resumed.Health.Count);
                Assert.All(resumed.Health.Values, sample => Assert.Equal((0L, false), sample));
            }
            finally { await resumed.StopAsync(CancellationToken.None); }
        }
        finally { await consumer.StopAsync(CancellationToken.None); }
    }

    private sealed class HealthProbe(IConfiguration config) : OperationalConsumer(config, NullLogger.Instance)
    {
        public ConcurrentDictionary<int, (long Lag, bool AfterEvent)> Health { get; } = new();
        public TaskCompletionSource EmptyHealth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HealthAfterEvent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int projected;
        public int Projected => Volatile.Read(ref projected);
        protected override string Group => "health-probe";
        protected override Task ProjectAsync(EventoOperativo value, string payload, CancellationToken ct)
        { Interlocked.Increment(ref projected); return Task.CompletedTask; }
        protected override Task HealthAsync(int partition, int partitions, long lag, CancellationToken ct)
        {
            Assert.Equal(2, partitions);
            Health[partition] = (lag, Projected > 0);
            if (Health.Count == 2 && Health.Values.All(x => x.Lag == 0))
            {
                EmptyHealth.TrySetResult();
                if (Health.Values.All(x => x.AfterEvent)) HealthAfterEvent.TrySetResult();
            }
            return Task.CompletedTask;
        }
    }
}
