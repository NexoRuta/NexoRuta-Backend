using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using NexoRuta.Application.Operacion;
using NexoRuta.Infrastructure.Operacion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Worker.Messaging;
namespace NexoRuta.IntegrationTests.Operacion;

[Collection(OperationalInfrastructureCollection.Name)]
public sealed class OperationalMongoIsolationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RealDocumentQueriesIsolateOperatorAndCommerceAndDoNotCountReplayOrExpiredDocuments()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)); var ct = deadline.Token;
        await using var mongo = new ContainerBuilder("mongo:8.0").WithPortBinding(27017, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(27017)).Build(); await mongo.StartAsync(ct);
        var uri = $"mongodb://{mongo.Hostname}:{mongo.GetMappedPublicPort(27017)}/?serverSelectionTimeoutMS=500&connectTimeoutMS=500";
        var name = "isolation_" + Guid.NewGuid().ToString("N"); var store = new OperationalMongoStore(new MongoClient(uri).GetDatabase(name));
        var data = await postgres.CreateDatabaseAsync(); using (var seed = data.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        Guid op, commerce, operatorAccess, commerceAccess;
        await using (var db = data.CreateDbContext())
        { var operatorActor = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null, ct); var commerceActor = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null, ct); op = operatorActor.OperadorId!.Value; commerce = commerceActor.ComercioId!.Value; operatorAccess = operatorActor.Id; commerceAccess = commerceActor.Id; }
        var otherOperator = Guid.NewGuid(); var otherCommerce = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        EventoOperativo Shipment(Guid tenant, Guid merchant, DateTimeOffset recorded)
        { var shipment = Guid.NewGuid(); return new(1, Guid.NewGuid(), tenant, merchant, "Envio", shipment, shipment, null, 0, Guid.NewGuid(), null, recorded, JsonSerializer.SerializeToElement(new { estado = "Admitido" })); }
        var own = Shipment(op, commerce, now); var otherMerchant = Shipment(op, otherCommerce, now); var otherTenant = Shipment(otherOperator, commerce, now); var expired = Shipment(op, commerce, now.AddDays(-91));
        foreach (var value in new[] { own, otherMerchant, own, otherTenant, expired, expired }) await store.InsertAsync(value, JsonSerializer.Serialize(value, OperationalConsumer.Json), ct);
        var ownRows = await store.QueryAsync(op, commerce, null, null, now.AddDays(-1), now.AddDays(1), 0, 100, ct); Assert.Single(ownRows);
        Assert.Equal(own.EventId, JsonSerializer.Deserialize<EventoOperativo>(ownRows[0], OperationalConsumer.Json)!.EventId);
        var first = await store.QueryAsync(op, null, null, null, now.AddDays(-1), now.AddDays(1), 0, 1, ct); var second = await store.QueryAsync(op, null, null, null, now.AddDays(-1), now.AddDays(1), 1, 1, ct);
        Assert.NotEqual(first[0], second[0]);
        Assert.Equal(1, (await store.VolumesAsync(op, commerce, now.AddDays(-1), now.AddDays(1), ct))["Envio"]);
        var mismatch = own with { Datos = JsonSerializer.SerializeToElement(new { estado = "Entregado" }) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InsertAsync(mismatch, JsonSerializer.Serialize(mismatch, OperationalConsumer.Json), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(op, commerce, null, null, now.AddDays(-32), now, 0, 100, ct));
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data); var settings = RlsRestrictedHttpFlowTests.ApiSettings(); settings["DatabaseInitialization:SeedData"] = "false"; settings["MongoDb:ConnectionString"] = uri; settings["MongoDb:Database"] = name;
        using var factory = new IntegrationApiFactory(runtime, settings: settings, seedOperationalRules: false); using var client = factory.CreateHttpClient();
        var query = $"desde={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&hasta={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        await client.SetBearerForAccessAsync(data, operatorAccess);
        var all = await client.GetFromJsonAsync<JsonElement>($"/api/operacion/historial?operadorId={op}&{query}", ct); Assert.Equal(2, all.GetProperty("datos").GetArrayLength());
        using (var forbidden = await client.GetAsync($"/api/operacion/historial?operadorId={otherOperator}&{query}", ct)) Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await client.SetBearerForAccessAsync(data, commerceAccess);
        var limited = await client.GetFromJsonAsync<JsonElement>($"/api/operacion/historial?operadorId={op}&{query}", ct); Assert.Single(limited.GetProperty("datos").EnumerateArray()); Assert.NotEqual("Actualizada", limited.GetProperty("estadoProyeccion").GetString());
        var volumes = await client.GetFromJsonAsync<JsonElement>($"/api/operacion/analitica/volumen?operadorId={op}&{query}", ct); Assert.Equal(1, volumes.GetProperty("volumenPorTipo").GetProperty("Envio").GetInt64());
        await mongo.StopAsync(ct);
        using var unavailable = await client.GetAsync($"/api/operacion/historial?operadorId={op}&{query}", ct); Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
    }
}
