using System.Text.Json;
using Confluent.Kafka;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using NexoRuta.Infrastructure.Administracion.Autenticacion;

namespace NexoRuta.Worker.Messaging;

public sealed class PasswordRecoveryRequestedDocument
{
    [BsonElement("eventId")]
    public string EventId { get; init; } = string.Empty;

    [BsonElement("schemaVersion")]
    public int SchemaVersion { get; init; }

    [BsonElement("usuarioId")]
    public string UsuarioId { get; init; } = string.Empty;

    [BsonElement("occurredAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAt { get; init; }

    public static PasswordRecoveryRequestedDocument From(PasswordRecoveryRequestedV1 value) => new()
    {
        EventId = value.EventId.ToString("D"),
        SchemaVersion = value.SchemaVersion,
        UsuarioId = value.UsuarioId.ToString("D"),
        OccurredAt = value.OccurredAt.UtcDateTime
    };
}

public sealed class PasswordRecoveryMongoStore(IMongoDatabase database, IConfiguration configuration)
{
    public const int RetentionDays = 90;
    private readonly string collectionName = configuration[WorkerConfigurationKeys.MongoCollection]
        ?? throw new InvalidOperationException("MongoDB recovery collection is required.");
    private IMongoCollection<PasswordRecoveryRequestedDocument> Collection =>
        database.GetCollection<PasswordRecoveryRequestedDocument>(collectionName);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var names = await database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = new BsonDocument("name", collectionName) }, cancellationToken);
        if (!await names.AnyAsync(cancellationToken))
            await database.CreateCollectionAsync(collectionName, cancellationToken: cancellationToken);

        var indexes = new[]
        {
            new CreateIndexModel<PasswordRecoveryRequestedDocument>(
                Builders<PasswordRecoveryRequestedDocument>.IndexKeys.Ascending(x => x.EventId),
                new CreateIndexOptions { Name = "ux_password_recovery_event_id", Unique = true }),
            new CreateIndexModel<PasswordRecoveryRequestedDocument>(
                Builders<PasswordRecoveryRequestedDocument>.IndexKeys.Ascending(x => x.OccurredAt),
                new CreateIndexOptions { Name = "ttl_password_recovery_occurred_at", ExpireAfter = TimeSpan.FromDays(RetentionDays) })
        };
        await Collection.Indexes.CreateManyAsync(indexes, cancellationToken);
    }

    public async Task InsertIdempotentlyAsync(PasswordRecoveryRequestedDocument document, CancellationToken cancellationToken)
    {
        try
        {
            await Collection.InsertOneAsync(document, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The unique eventId index makes Kafka redelivery an idempotent no-op.
        }
    }
}

public sealed class PasswordRecoveryMongoConsumer(
    IConfiguration configuration,
    PasswordRecoveryMongoStore store,
    ILogger<PasswordRecoveryMongoConsumer> logger) : BackgroundService
{
    public const string ConsumerGroupId = "nexoruta.password-recovery.mongo.v1";
    private const int RetryDelaySeconds = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.InitializeAsync(stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("MongoDB recovery store is unavailable. ExceptionType: {ExceptionType}; initialization will retry.",
                    error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken);
            }
        }

        var servers = configuration[WorkerConfigurationKeys.KafkaBootstrapServers]
            ?? throw new InvalidOperationException("Kafka bootstrap servers are required.");
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = servers,
            GroupId = ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = false,
            SecurityProtocol = configuration.GetValue<SecurityProtocol?>(WorkerConfigurationKeys.KafkaSecurityProtocol)
        };
        using var consumer = new ConsumerBuilder<string, string>(consumerConfig)
            .SetLogHandler((_, _) => { })
            .Build();
        consumer.Subscribe(PasswordRecoveryMessaging.RequestedTopic);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? message = null;
            try
            {
                message = consumer.Consume(stoppingToken);
                var value = JsonSerializer.Deserialize<PasswordRecoveryRequestedV1>(message.Message.Value, PasswordRecoveryJson.Options)
                    ?? throw new InvalidDataException("Password recovery event payload is invalid.");
                if (value.EventId == Guid.Empty || value.UsuarioId == Guid.Empty || value.OccurredAt == default ||
                    value.SchemaVersion != PasswordRecoveryEventVersion.Current ||
                    !string.Equals(message.Message.Key, value.EventId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Password recovery event contract is invalid.");

                await store.InsertIdempotentlyAsync(PasswordRecoveryRequestedDocument.From(value), stoppingToken);
                consumer.Commit(message);
                logger.LogInformation("Password recovery request persisted. EventId: {EventId}", value.EventId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Kafka recovery event was not committed. ExceptionType: {ExceptionType}; retrying persistence.",
                    error.GetType().Name);
                if (message is not null)
                {
                    try { consumer.Seek(message.TopicPartitionOffset); }
                    catch (KafkaException seekError)
                    {
                        logger.LogWarning("Kafka recovery seek failed. ErrorCode: {ErrorCode}", seekError.Error.Code);
                    }
                }
                try { await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }

        consumer.Close();
    }
}
