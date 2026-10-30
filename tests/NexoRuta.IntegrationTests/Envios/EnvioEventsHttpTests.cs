using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class EnvioEventsHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task HistoryAndLaterOutboxFailuresRollbackTheWholeShipmentCreation()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string commerceEmail = "shipment-history-rollback@example.test";
        Guid operatorId;
        await using (var seed = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(seed,
                new AccesoInicial(commerceEmail, "History operator", "History commerce", "history-operator@example.test"))
                .SeedAsync();
            await ReglasEnvioTestData.SembrarInicialesAsync(seed);
            operatorId = await seed.Operadores.Select(x => x.Id).SingleAsync();
        }

        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = api.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, commerceEmail));

        await using (var owner = database.CreateDbContext())
            await owner.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"EnvioEventos\" ADD CONSTRAINT audit_reject_creation CHECK (\"Tipo\" <> 'Creacion')");
        var historyFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            client.PostAsJsonAsync("/api/envios", Alta(operatorId, "HISTORY-FAIL")));
        Assert.Equal("audit_reject_creation", historyFailure.ConstraintName);
        await AssertEmptyAsync(database);

        await using (var owner = database.CreateDbContext())
        {
            await owner.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"EnvioEventos\" DROP CONSTRAINT audit_reject_creation");
            await owner.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"OutboxMessages\" ADD CONSTRAINT audit_reject_outbox CHECK (false)");
        }
        var outboxFailure = await Assert.ThrowsAsync<DbUpdateException>(() =>
            client.PostAsJsonAsync("/api/envios", Alta(operatorId, "OUTBOX-FAIL")));
        Assert.Equal("audit_reject_outbox", Assert.IsType<PostgresException>(outboxFailure.InnerException).ConstraintName);
        await AssertEmptyAsync(database);

        await using (var owner = database.CreateDbContext())
            await owner.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"OutboxMessages\" DROP CONSTRAINT audit_reject_outbox");
        using var created = await client.PostAsJsonAsync("/api/envios", Alta(operatorId, "CANCEL-HISTORY-FAIL"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shipmentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        int outboxBeforeCancellation;
        await using (var owner = database.CreateDbContext())
        {
            outboxBeforeCancellation = await owner.OutboxMessages.CountAsync();
            await owner.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"EnvioEventos\" ADD CONSTRAINT audit_reject_transition CHECK (\"Tipo\" <> 'TransicionEstado')");
        }

        using var operatorClient = api.CreateHttpClient();
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await LoginAsync(operatorClient, "history-operator@example.test"));
        var cancellationFailure = await Assert.ThrowsAsync<PostgresException>(() =>
            operatorClient.PostAsJsonAsync($"/api/envios/{shipmentId}/cancelacion", new { Version = 0, Motivo = "History rollback test." }));
        Assert.Equal("audit_reject_transition", cancellationFailure.ConstraintName);
        await using (var verify = database.CreateDbContext())
        {
            Assert.Equal(EstadoEnvio.Admitido, (await verify.Envios.SingleAsync(x => x.Id == shipmentId)).Estado);
            Assert.Empty(await verify.CancelacionesEnvio.Where(x => x.EnvioId == shipmentId).ToArrayAsync());
            Assert.Single(await verify.EnvioEventos.Where(x => x.EnvioId == shipmentId).ToArrayAsync());
            Assert.Equal(outboxBeforeCancellation, await verify.OutboxMessages.CountAsync());
        }
    }

    private static async Task AssertEmptyAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.Envios.ToArrayAsync());
        Assert.Empty(await db.Destinatarios.ToArrayAsync());
        Assert.Empty(await db.Direcciones.ToArrayAsync());
        Assert.Empty(await db.Bultos.ToArrayAsync());
        Assert.Empty(await db.EnvioEventos.ToArrayAsync());
        Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }

    private static object Alta(Guid operatorId, string code) => new
    {
        OperadorId = operatorId,
        DestinatarioNombre = "Ada Lovelace",
        DestinatarioEmail = "recipient@example.test",
        Direccion = "18 de Julio 123",
        CodigoBulto = code,
        PesoGramos = 500m,
        LargoCentimetros = 20m,
        AnchoCentimetros = 10m,
        AltoCentimetros = 5m
    };

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
}
