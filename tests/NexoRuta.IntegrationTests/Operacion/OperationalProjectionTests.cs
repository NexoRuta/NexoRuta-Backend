using System.Text.Json;
using Xunit.Abstractions;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NexoRuta.Application.Operacion;
using NexoRuta.Infrastructure.Operacion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker.Messaging;

namespace NexoRuta.IntegrationTests.Operacion;

[Collection(OperationalInfrastructureCollection.Name)]
public sealed class OperationalProjectionTests(PostgresFixture postgres, KafkaFixture kafka, ITestOutputHelper output)
    : IClassFixture<PostgresFixture>, IClassFixture<KafkaFixture>
{
    [Fact]
    public async Task IndependentConsumersDeduplicateLatePositionsAndReplayIntoIsolatedDestinations()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        await using var mongo = new ContainerBuilder("mongo:8.0")
            .WithPortBinding(27017, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(27017)).Build();
        await mongo.StartAsync(ct);
        var mongoClient = new MongoClient($"mongodb://{mongo.Hostname}:{mongo.GetMappedPublicPort(27017)}/?serverSelectionTimeoutMS=3000&socketTimeoutMS=1000&connectTimeoutMS=1000");
        var topic = "test-operational-" + Guid.NewGuid().ToString("N");
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 2, ReplicationFactor = 1, Configs = new Dictionary<string, string> { ["retention.ms"] = "-1", ["retention.bytes"] = "-1" } }]);
        var first = await postgres.CreateDatabaseAsync();
        var replay = await postgres.CreateDatabaseAsync();
        var firstMongo = mongoClient.GetDatabase("first_" + Guid.NewGuid().ToString("N"));
        var replayMongo = mongoClient.GetDatabase("replay_" + Guid.NewGuid().ToString("N"));
        var opA = Guid.NewGuid(); var opB = Guid.NewGuid(); var route = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        EventoOperativo Position(Guid op, long version, DateTimeOffset physical, double latitude) => new(1,
            Guid.NewGuid(), op, null, "Posicion", route, null, route, version, Guid.NewGuid(), physical, now,
            JsonSerializer.SerializeToElement(new { latitud = latitude, longitud = -56d, precisionMetros = 10d }));
        var latest = Position(opA, 2, now, -34.8);
        var late = Position(opA, 3, now.AddMinutes(-2), -34.9);
        var otherTenant = Position(opB, 1, now, -32d);
        var commerce=Guid.NewGuid();var shipment=Guid.NewGuid();var vehicle=Guid.NewGuid();
        EventoOperativo Fact(string type,Guid entity,long version,object data,Guid? shipmentId=null)=>new(1,Guid.NewGuid(),opA,
            shipmentId is null?null:commerce,type,entity,shipmentId,route,version,Guid.NewGuid(),null,now,JsonSerializer.SerializeToElement(data));
        var partial=Fact("Envio",shipment,3,new { estado="EntregadoParcialmente",esRetiro=false,compromisoHastaUtc=now.AddHours(1) },shipment);
        var previous=Fact("Envio",shipment,2,new { estado="EnTransito",esRetiro=false },shipment);
        var facts=new[] {
            latest,late,latest,otherTenant,Position(opA,4,now.AddMinutes(1),-36),
            partial,previous,
            Fact("Bulto",Guid.NewGuid(),2,new { resultado="Entregado",custodia="Destinatario" },shipment),
            Fact("Bulto",Guid.NewGuid(),0,new { resultado="Pendiente",custodia="Repartidor" },shipment),
            Fact("Ruta",route,2,new { estado="EnCurso",vehiculoId=vehicle,repartidorAccesoId=Guid.NewGuid() }),
            Fact("Parada",Guid.NewGuid(),0,new { activa=true,resultadoVisita="Pendiente",llegadaEstimadaUtc=now.AddMinutes(15) },shipment),
            Fact("Estimacion",route,1,new { motivo="Calculada" }),
            Fact("Flota",vehicle,1,new { accion="Salida" })
        };
        var expected=facts.Select(x=>x.EventId).Distinct().Count();
        using (var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers, Acks = Acks.All }).Build())
            foreach (var value in facts)
                await producer.ProduceAsync(topic, new Message<string, string> { Key = value.OperadorId.ToString("D") + ":" + value.EntidadId.ToString("D"), Value = JsonSerializer.Serialize(value, OperationalConsumer.Json) }, ct);
        // Only the documentary consumer fails. The independent board must catch up
        // while MongoDB is paused; then the same group recovers its uncommitted offset.
        await mongo.PauseAsync(ct);
        var firstGroups = await ConsumeAsync(first, firstMongo, topic, expected, ct, () => mongo.UnpauseAsync(ct));
        var replayGroups = await ConsumeAsync(replay, replayMongo, topic, expected, ct);
        var stateA = await StateAsync(first, ct);
        var stateB = await StateAsync(replay, ct);
        Assert.Equal(stateA, stateB); // Entire materialized state, excluding destination-local revision IDs.
        using var db = first.CreateDbContext();
        Assert.Equal(expected, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM public.\"OperacionEventosProcesados\"").SingleAsync(ct));
        var eventJson = await db.Database.SqlQuery<string>($"SELECT \"Evento\"::text AS \"Value\" FROM public.\"OperacionProyeccion\" WHERE \"OperadorId\"={opA} AND \"Tipo\"='Posicion'").SingleAsync(ct);
        Assert.Equal(latest.EventId, JsonSerializer.Deserialize<EventoOperativo>(eventJson, OperationalConsumer.Json)!.EventId);
        var store = new OperationalMongoStore(firstMongo);
        var history = await store.QueryAsync(opA, null, null, "Posicion", now.AddDays(-1), now.AddDays(1), 0, 100, ct);
        Assert.Equal(3, history.Count); // older physical position is preserved in history, never overwrites current.
        Assert.All(history, x => Assert.Equal(opA, JsonSerializer.Deserialize<EventoOperativo>(x, OperationalConsumer.Json)!.OperadorId));
        var currentShipment = await db.Database.SqlQuery<string>($"SELECT \"Evento\"::text AS \"Value\" FROM public.\"OperacionProyeccion\" WHERE \"OperadorId\"={opA} AND \"Tipo\"='Envio'").SingleAsync(ct);
        Assert.Equal(partial.EventId,JsonSerializer.Deserialize<EventoOperativo>(currentShipment,OperationalConsumer.Json)!.EventId);
        Assert.Equal(9,stateA.Length);
        output.WriteLine(JsonSerializer.Serialize(new { topic, firstGroups, replayGroups, uniqueEvents = expected, materializedRows = stateA.Length, identicalReplay = stateA.SequenceEqual(stateB), physicalPositionNeverRewinds = true, duplicatesCountOnce = true, documentaryConsumerRecoveredIndependently = true }));
        Assert.Single(await store.QueryAsync(opB, null, null, null, now.AddDays(-1), now.AddDays(1), 0, 100, ct));
    }

    private async Task<string[]> ConsumeAsync(IntegrationDatabase database, IMongoDatabase mongo, string topic, int expected, CancellationToken ct, Func<Task>? recover = null)
    {
        await using var provider = new ServiceCollection().AddDbContext<NexoRutaDbContext>(options => options.UseNexoRutaPostgres(database.ConnectionString)).BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Operational:Topic"] = topic,
            ["Operational:Partitions"] = "2",
            ["Operational:nexoruta-operational-board-v1:Group"] = "board-" + Guid.NewGuid().ToString("N"),
            ["Operational:nexoruta-operational-history-v1:Group"] = "history-" + Guid.NewGuid().ToString("N")
        }).Build();
        using var board = new OperationalBoardConsumer(config, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalBoardConsumer>.Instance);
        var recoveryExpected = recover is not null;
        var historyLog = new HistoryFailureLogger();
        using var history = new OperationalHistoryConsumer(config, new OperationalMongoStore(mongo), provider.GetRequiredService<IServiceScopeFactory>(), historyLog);
        await board.StartAsync(ct);
        await history.StartAsync(ct);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var db = database.CreateDbContext();
                var count = await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM public.\"OperacionEventosProcesados\"").SingleAsync(ct);
                if (recover is not null)
                {
                    if (count != expected) { await Task.Delay(100, ct); continue; }
                    await historyLog.FirstFailure.Task.WaitAsync(ct);
                    await recover();
                    recover = null;
                }
                long documents;
                try { documents = await mongo.GetCollection<BsonDocument>(OperationalMongoStore.CollectionName).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct); }
                catch (Exception error) when (error is MongoException or TimeoutException)
                { await Task.Delay(100, ct); continue; }
                if (count == expected && documents == expected) break;
                await Task.Delay(100, ct);
            }
        }
        finally { await board.StopAsync(CancellationToken.None); await history.StopAsync(CancellationToken.None); }
        if (recoveryExpected) Assert.True(historyLog.FirstFailure.Task.IsCompletedSuccessfully);
        return [config["Operational:nexoruta-operational-board-v1:Group"]!, config["Operational:nexoruta-operational-history-v1:Group"]!];
    }

    private sealed class HistoryFailureLogger : ILogger<OperationalHistoryConsumer>
    {
        public TaskCompletionSource FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Warning) FirstFailure.TrySetResult(); }
    }

    private static async Task<string[]> StateAsync(IntegrationDatabase database, CancellationToken ct)
    {
        using var db = database.CreateDbContext();
        return await db.Database.SqlQueryRaw<string>("SELECT \"Evento\"::text AS \"Value\" FROM public.\"OperacionProyeccion\" ORDER BY \"OperadorId\",\"Tipo\",\"EntidadId\"").ToArrayAsync(ct);
    }
}
