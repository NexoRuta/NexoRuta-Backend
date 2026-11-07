using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using NexoRuta.Application.Operacion;
using NexoRuta.Application.Operacion.Exceptions;
using NexoRuta.Application.Operacion.Repositories;

namespace NexoRuta.Infrastructure.Operacion;

public sealed class OperationalMongoStore(IMongoDatabase database, TimeProvider clock) : IHistorialOperativoRepository
{
    public const string CollectionName = "operational_history_v1";
    private readonly SemaphoreSlim initialization = new(1, 1);
    private bool initialized;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
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

    public async Task InsertAsync(EventoOperativo value, CancellationToken ct)
    {
        value.Validar();
        await InitializeAsync(ct);
        var payload = JsonSerializer.Serialize(value, Json);
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
            ["expiresAt"] = value.RegistradaEnUtc.UtcDateTime.AddDays(PoliticaHistorialOperativo.RetencionDias),
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
            if (!JsonElement.DeepEquals(previous.RootElement, incoming.RootElement)
                || !SameMetadata(existing, doc))
                throw new InvalidDataException("Event identity reused with different content.");
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> VolumesAsync(Guid operatorId, Guid? commerceId,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (operatorId == Guid.Empty || commerceId == Guid.Empty || from >= to || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("Analytics require a tenant and a range of at most 31 days.");
        var now = clock.GetUtcNow();
        var filter = new BsonDocument
        {
            ["operadorId"] = operatorId.ToString("D"),
            ["registradaEnUtc"] = new BsonDocument { ["$gte"] = from.UtcDateTime, ["$lt"] = to.UtcDateTime },
            ["expiresAt"] = new BsonDocument("$gt", now.UtcDateTime)
        };
        if (commerceId is { } commerce) filter["comercioId"] = commerce.ToString("D");
        try
        {
            var groups = await Collection.Aggregate(new AggregateOptions { MaxTime = TimeSpan.FromSeconds(3) })
                .Match(filter).Group(new BsonDocument { ["_id"] = "$tipo", ["eventos"] = new BsonDocument("$sum", 1) })
                .Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            return groups.ToDictionary(x => x["_id"].AsString, x => x["eventos"].ToInt64());
        }
        catch (Exception error) when (error is MongoException or TimeoutException)
        { throw new HistorialOperativoNoDisponibleException(innerException: error); }
    }

    public async Task<IReadOnlyList<EventoOperativo>> QueryAsync(Guid operatorId, Guid? commerceId, Guid? shipmentId,
        string? type, DateTimeOffset from, DateTimeOffset to, int offset, int limit, CancellationToken ct)
    {
        if (operatorId == Guid.Empty || commerceId == Guid.Empty || shipmentId == Guid.Empty || limit is < 1 or > 100
            || offset is < 0 or > 10000 || from >= to || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("History queries require a tenant, 1-100 rows, offset <=10000 and a range of at most 31 days.");
        var now = clock.GetUtcNow();
        var filter = new BsonDocument
        {
            ["operadorId"] = operatorId.ToString("D"),
            ["registradaEnUtc"] = new BsonDocument { ["$gte"] = from.UtcDateTime, ["$lt"] = to.UtcDateTime },
            ["expiresAt"] = new BsonDocument("$gt", now.UtcDateTime)
        };
        if (commerceId is { } commerce) filter["comercioId"] = commerce.ToString("D");
        if (shipmentId is { } shipment) filter["envioId"] = shipment.ToString("D");
        if (type is not null) filter["tipo"] = type;
        try
        {
            var docs = await Collection.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(3) })
                .Sort(new BsonDocument { ["registradaEnUtc"] = -1, ["_id"] = -1 }).Skip(offset).Limit(limit).ToListAsync(ct);
            return docs.Select(x => JsonSerializer.Deserialize<EventoOperativo>(x["payload"].ToJson(
                new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }), Json)
                ?? throw new InvalidDataException("Stored operational event is empty.")).ToArray();
        }
        catch (Exception error) when (error is MongoException or TimeoutException)
        { throw new HistorialOperativoNoDisponibleException(innerException: error); }
    }

    private static bool SameMetadata(BsonDocument existing, BsonDocument incoming)
        => existing["_id"] == incoming["_id"]
           && existing["operadorId"] == incoming["operadorId"]
           && existing["comercioId"] == incoming["comercioId"]
           && existing["envioId"] == incoming["envioId"]
           && existing["tipo"] == incoming["tipo"]
           && existing["version"] == incoming["version"]
           && existing["correlacionId"] == incoming["correlacionId"]
           && existing["registradaEnUtc"] == incoming["registradaEnUtc"]
           && existing["ocurridaEnUtc"] == incoming["ocurridaEnUtc"]
           && existing["expiresAt"] == incoming["expiresAt"];
}
