using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Operacion;
using NexoRuta.Infrastructure.Operacion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Worker.Messaging;
namespace NexoRuta.IntegrationTests.Operacion;

[Collection(OperationalInfrastructureCollection.Name)]
public sealed class OperationalSignalRTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task TwoRealKestrelApisShareTenantScopedPhotographsAndReconnectWithoutRewinding()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)); var ct = deadline.Token;
        var database = await postgres.CreateDatabaseAsync();
        using (var seed = database.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings(); settings["DatabaseInitialization:SeedData"] = "false";
        using var first = new IntegrationApiFactory(runtime, settings: settings, seedOperationalRules: false);
        using var second = new IntegrationApiFactory(runtime, settings: settings, seedOperationalRules: false);
        first.UseKestrel(0); second.UseKestrel(0);
        using var clientA = first.CreateClient(); using var clientB = second.CreateClient();
        Guid accessId, op;
        await using (var db = database.CreateDbContext()) { var actor = await db.AccesosUsuario.FirstAsync(x => x.OperadorId != null, ct); accessId = actor.Id; op = actor.OperadorId!.Value; }
        await clientA.SetBearerForAccessAsync(database, accessId); await clientB.SetBearerForAccessAsync(database, accessId);
        var token = clientA.DefaultRequestHeaders.Authorization!.Parameter;
        await using var connectionA = new HubConnectionBuilder().WithUrl(new Uri(clientA.BaseAddress!, "hubs/operacion"), o => o.AccessTokenProvider = () => Task.FromResult(token)).Build();
        await using var connectionB = new HubConnectionBuilder().WithUrl(new Uri(clientB.BaseAddress!, "hubs/operacion"), o => o.AccessTokenProvider = () => Task.FromResult(token)).Build();
        var route = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        EventoOperativo Position(Guid tenant, long version, DateTimeOffset physical, double latitude) => new(1, Guid.NewGuid(), tenant, null, "Posicion", route, null, route, version, Guid.NewGuid(), physical, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { latitud = latitude, longitud = -56d, precisionMetros = 10d }));
        var initial = Position(op, 1, now, -34.8); var other = Position(Guid.NewGuid(), 1, now, -31.0);
        await Apply(initial); await Apply(other);
        await Task.WhenAll(connectionA.StartAsync(ct), connectionB.StartAsync(ct));
        var snapshotA = await Photograph(connectionA, ct); var snapshotB = await Photograph(connectionB, ct);
        Assert.Single(snapshotA.Entidades); Assert.Single(snapshotB.Entidades);
        Assert.Equal(initial.EventId, snapshotA.Entidades[0].EventId); Assert.Equal(snapshotA.Revision, snapshotB.Revision);
        await connectionA.StopAsync(ct);
        var newer = Position(op, 2, now.AddMilliseconds(1), -34.7); await Apply(newer);
        var delayed = Position(op, 3, now.AddMinutes(-2), -35); await Apply(delayed); await Apply(newer);
        var onSecond = await Photograph(connectionB, ct); Assert.Equal(newer.EventId, Assert.Single(onSecond.Entidades).EventId);
        await connectionA.StartAsync(ct);
        var reconnected = await Photograph(connectionA, ct);
        Assert.True(reconnected.Revision > snapshotA.Revision); Assert.Equal(onSecond.Revision, reconnected.Revision);
        Assert.All(reconnected.Entidades, x => Assert.Equal(op, x.OperadorId));
        Assert.Equal(newer.EventId, Assert.Single(reconnected.Entidades).EventId);
        using var anonymous = new HttpClient { BaseAddress = clientA.BaseAddress };
        using var denied = await anonymous.PostAsync("/hubs/operacion/negotiate?negotiateVersion=1", null, ct);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);

        async Task Apply(EventoOperativo value)
        { await using var db = database.CreateDbContext(); var json = JsonSerializer.Serialize(value, OperationalConsumer.Json); await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.project_operational_event({json}::jsonb)", ct); }
    }
    private static async Task<OperationalSnapshot> Photograph(HubConnection connection, CancellationToken ct)
    {
        await using var stream = connection.StreamAsync<OperationalSnapshot>("Fotografias", ct).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct)); return stream.Current;
    }

    [Fact]
    public async Task PublicContractPublishesHistoryParcelsAndNullableEstimate()
    {
        var database = await postgres.CreateDatabaseAsync(); using var api = new IntegrationApiFactory(database, "Development"); using var client = api.CreateClient();
        var json = await client.GetStringAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(json);
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("SeguimientoEstadoResponse").GetProperty("properties");
        Assert.True(schema.TryGetProperty("historial", out _)); Assert.True(schema.TryGetProperty("bultos", out _)); Assert.True(schema.TryGetProperty("ventanaEstimada", out _));
        if (Environment.GetEnvironmentVariable("NEXORUTA_OPENAPI_OUTPUT") is { Length: > 0 } output) await File.WriteAllTextAsync(output, json);
    }
}
