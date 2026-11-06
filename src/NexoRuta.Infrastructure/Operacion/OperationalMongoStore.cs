using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using NexoRuta.Application.Operacion;

namespace NexoRuta.Infrastructure.Operacion;

public sealed class OperationalMongoStore(IMongoDatabase database)
{
    public const string CollectionName = "operational_history_v1";
    public const int RetentionDays = 90;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private bool initialized;
    private IMongoCollection<BsonDocument> Collection => database.GetCollection<BsonDocument>(CollectionName).WithReadPreference(ReadPreference.Primary).WithWriteConcern(WriteConcern.WMajority);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await initialization.WaitAsync(ct);
        try
        {
            if (initialized) return;
            var keys = Builders<BsonDocument>.IndexKeys;
            await Collection.Indexes.CreateManyAsync([
                new(keys.Ascending("operadorId").Descending("registradaEnUtc").Descending("_id"), new CreateIndexOptions { Name = "operator_history" }),
                new(keys.Ascending("operadorId").Ascending("envioId").Descending("registradaEnUtc").Descending("_id"), new CreateIndexOptions { Name = "operator_shipment_history" }),
                new(keys.Ascending("operadorId").Ascending("comercioId").Descending("registradaEnUtc").Descending("_id"), new CreateIndexOptions { Name = "tenant_history" }),
                new(keys.Ascending("operadorId").Ascending("comercioId").Ascending("envioId").Descending("registradaEnUtc").Descending("_id"), new CreateIndexOptions { Name = "shipment_history" }),
                new(keys.Ascending("expiresAt"), new CreateIndexOptions { Name = "history_retention", ExpireAfter = TimeSpan.Zero })
            ], ct);
            initialized = true;
        }
        finally { initialization.Release(); }
    }

    public async Task InsertAsync(EventoOperativo value, string payload, CancellationToken ct)
    {
        value.Validar();
        await InitializeAsync(ct);
        // TTL is anchored to original recording, so replay never extends retention.
        var doc = new BsonDocument
        {
            ["_id"] = value.EventId.ToString("D"),
            ["operadorId"] = value.OperadorId.ToString("D"),
            ["comercioId"] = value.ComercioId?.ToString("D") is { } commerce ? commerce : BsonNull.Value,
            ["envioId"] = value.EnvioId?.ToString("D") is { } shipment ? shipment : BsonNull.Value,
            ["tipo"] = value.Tipo,
            ["version"] = value.Version,
            ["correlacionId"] = value.CorrelacionId.ToString("D"),
            ["registradaEnUtc"] = value.RegistradaEnUtc.UtcDateTime,
            ["ocurridaEnUtc"] = value.OcurridaEnUtc is { } physical ? physical.UtcDateTime : BsonNull.Value,
            ["expiresAt"] = value.RegistradaEnUtc.UtcDateTime.AddDays(RetentionDays),
            ["payload"] = BsonDocument.Parse(payload)
        };
        try { await Collection.InsertOneAsync(doc, cancellationToken: ct); }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await Collection.Find(new BsonDocument("_id", value.EventId.ToString("D"))).SingleAsync(ct);
            // JSON object property order is not part of the event contract. Match
            // PostgreSQL jsonb semantics while still rejecting divergent content.
            var format = new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson };
            using var previous = JsonDocument.Parse(existing["payload"].ToJson(format));
            using var incoming = JsonDocument.Parse(doc["payload"].ToJson(format));
            if (!JsonElement.DeepEquals(previous.RootElement, incoming.RootElement))
                throw new InvalidDataException("Event identity reused with different content.");
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> VolumesAsync(Guid operatorId, Guid? commerceId,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (operatorId == Guid.Empty || commerceId == Guid.Empty || from >= to || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("Analytics require a tenant and a range of at most 31 days.");
        var filter = new BsonDocument
        {
            ["operadorId"] = operatorId.ToString("D"),
            ["registradaEnUtc"] = new BsonDocument { ["$gte"] = from.UtcDateTime, ["$lt"] = to.UtcDateTime },
            ["expiresAt"] = new BsonDocument("$gt", DateTime.UtcNow)
        };
        if (commerceId is { } commerce) filter["comercioId"] = commerce.ToString("D");
        var groups = await Collection.Aggregate(new AggregateOptions { MaxTime = TimeSpan.FromSeconds(3) })
            .Match(filter).Group(new BsonDocument { ["_id"] = "$tipo", ["eventos"] = new BsonDocument("$sum", 1) })
            .Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        return groups.ToDictionary(x => x["_id"].AsString, x => x["eventos"].ToInt64());
    }

    public async Task<IReadOnlyList<string>> QueryAsync(Guid operatorId, Guid? commerceId, Guid? shipmentId,
        string? type, DateTimeOffset from, DateTimeOffset to, int offset, int limit, CancellationToken ct)
    {
        if (operatorId == Guid.Empty || commerceId == Guid.Empty || shipmentId == Guid.Empty || limit is < 1 or > 100
            || offset is < 0 or > 10000 || from >= to || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("History queries require a tenant, 1-100 rows, offset <=10000 and a range of at most 31 days.");
        var filter = new BsonDocument
        {
            ["operadorId"] = operatorId.ToString("D"),
            ["registradaEnUtc"] = new BsonDocument { ["$gte"] = from.UtcDateTime, ["$lt"] = to.UtcDateTime },
            ["expiresAt"] = new BsonDocument("$gt", DateTime.UtcNow)
        };
        if (commerceId is { } commerce) filter["comercioId"] = commerce.ToString("D");
        if (shipmentId is { } shipment) filter["envioId"] = shipment.ToString("D");
        if (type is not null) filter["tipo"] = type;
        var docs = await Collection.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(3) })
            .Sort(new BsonDocument { ["registradaEnUtc"] = -1, ["_id"] = -1 }).Skip(offset).Limit(limit).ToListAsync(ct);
        return docs.Select(x => x["payload"].ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson })).ToArray();
    }
}
