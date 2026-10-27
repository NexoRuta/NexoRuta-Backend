using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Envios;
using Xunit.Abstractions;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class EnvioHistoryMigrationTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task MigrationMarksTheCutoverAndBackfillsOnlyAuditedCancellations()
    {
        const int shipmentCount = 32;
        var database = await postgres.CreateDatabaseAsync("20261025010937_HistoricalImportOperatorCatalog");
        const string commerceEmail = "shipment-history-backfill@example.test";
        Guid operatorId;
        Guid commerceId;
        Guid userId;
        Guid accessId;
        Guid canceledShipmentId;
        await using (var db = database.CreateDbContext())
        {
            // This migration fixture intentionally starts before the history/deadline migrations, but
            // seeding uses the current EF model. Add the later nullable mappings to this isolated
            // legacy schema and mark those migrations as applied so the history cutover under test
            // remains pending. Dedicated tests exercise the deadline migration on a normal upgrade.
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE public."VersionesReglasOperativas"
                    ADD COLUMN "PlazoValidacionPruebaMinutos" integer NULL;
                ALTER TABLE public."VersionesReglasOperativas"
                    ADD CONSTRAINT "CK_VersionesReglasOperativas_ProofValidationDeadline"
                    CHECK ("PlazoValidacionPruebaMinutos" IS NULL OR "PlazoValidacionPruebaMinutos" BETWEEN 1 AND 525600);
                ALTER TABLE public."Envios"
                    ADD COLUMN "EntregaFisicaRecibidaEnUtc" timestamp with time zone NULL,
                    ADD COLUMN "PlazoPruebaVenceEnUtc" timestamp with time zone NULL;
                ALTER TABLE public."Envios"
                    ADD CONSTRAINT "CK_Envios_PhysicalDeliveryReceiptTime"
                        CHECK ("EntregaFisicaRecibidaEnUtc" IS NULL OR "EntregaFisicaEnUtc" IS NOT NULL),
                    ADD CONSTRAINT "CK_Envios_ProofDeadlineAfterReceipt"
                        CHECK ("PlazoPruebaVenceEnUtc" IS NULL OR ("EntregaFisicaRecibidaEnUtc" IS NOT NULL
                            AND "PlazoPruebaVenceEnUtc" > "EntregaFisicaRecibidaEnUtc"));
                INSERT INTO public."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20261025233714_AddProofValidationDeadline', '10.0.0');
                INSERT INTO public."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20261025234203_PersistShipmentProofDeadline', '10.0.0');
                """);
            await AuthenticationTestData.Seeder(db,
                new AccesoInicial(commerceEmail, "History operator", "History commerce", "history-operator@example.test"))
                .SeedAsync();
            await ReglasEnvioTestData.SembrarInicialesAsync(db);
            var access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
            operatorId = await db.Operadores.Select(x => x.Id).SingleAsync();
            commerceId = access.ComercioId!.Value;
            userId = access.UsuarioId;
            accessId = access.Id;
            var ruleVersion = await db.VersionesReglasOperativas.SingleAsync(x => x.OperadorId == operatorId
                && x.Estado == EstadoVersionReglasOperativas.Publicada);

            var shipments = new List<Envio>();
            for (var i = 0; i < shipmentCount; i++)
            {
                var recipient = new Destinatario(operatorId, $"Recipient {i}", $"recipient-{i}@example.test");
                var address = new Direccion(operatorId, $"History address {i}");
                var shipment = new Envio(operatorId, commerceId, userId, recipient.Id, address.Id, ruleVersion.Id);
                var parcel = new Bulto(operatorId, shipment.Id, $"HIST-{i:D3}", 500m, 20m, 10m, 5m);
                db.AddRange(recipient, address, shipment, parcel);
                shipments.Add(shipment);
            }
            await db.SaveChangesAsync();
            canceledShipmentId = shipments[0].Id;
        }

        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = api.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, commerceEmail));
        using (var cancel = await client.PostAsJsonAsync($"/api/envios/{canceledShipmentId}/cancelacion",
            new { Version = 0, Motivo = "Audit-backed cutover fixture." }))
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        int outboxCount;
        Guid cancellationId;
        DateTimeOffset cancellationTime;
        await using (var db = database.CreateDbContext())
        {
            outboxCount = await db.OutboxMessages.CountAsync();
            var audit = await db.CancelacionesEnvio.SingleAsync(x => x.EnvioId == canceledShipmentId);
            cancellationId = audit.Id;
            cancellationTime = audit.CreadaEnUtc;
        }

        var stopwatch = Stopwatch.StartNew();
        await using (var db = database.CreateDbContext())
            await db.GetService<IMigrator>().MigrateAsync();
        stopwatch.Stop();
        output.WriteLine($"Cutover migration: {shipmentCount} shipments, one audited cancellation; {stopwatch.Elapsed.TotalMilliseconds:F0} ms on ephemeral PostgreSQL 18/PostGIS Testcontainers.");

        await using var verify = database.CreateDbContext();
        Assert.Equal(shipmentCount, await verify.EnvioEventos.CountAsync(x => x.Tipo == TipoEventoEnvio.EstadoObservadoInicio));
        Assert.Equal(0, await verify.EnvioEventos.CountAsync(x => x.Tipo == TipoEventoEnvio.Creacion));
        var cutover = await verify.EnvioEventos.SingleAsync(x => x.EnvioId == canceledShipmentId
            && x.Tipo == TipoEventoEnvio.EstadoObservadoInicio);
        Assert.Equal(EstadoEnvio.Cancelado.ToString(), cutover.EstadoObservado);
        Assert.Equal(1, cutover.VersionNueva);
        Assert.Equal("Migracion", cutover.ActorTipo);
        Assert.Null(cutover.ActorUsuarioId);
        Assert.Null(cutover.OcurridaEnUtc);

        var reconstructed = await verify.EnvioEventos.SingleAsync(x => x.CancelacionEnvioId == cancellationId);
        Assert.Equal(("Backfill", "CancelacionEnvio", "Admitido", "Cancelado", 0L, 1L),
            (reconstructed.Procedencia, reconstructed.OrigenTipo, reconstructed.EstadoAnterior,
                reconstructed.EstadoNuevo, reconstructed.VersionAnterior, reconstructed.VersionNueva));
        Assert.Equal(cancellationTime, reconstructed.OcurridaEnUtc);
        Assert.Equal(cancellationId, reconstructed.OrigenId);
        Assert.Equal(outboxCount, await verify.OutboxMessages.CountAsync());
        Assert.Equal(1, await verify.EnvioEventos.CountAsync(x => x.CancelacionEnvioId == cancellationId));
    }

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
