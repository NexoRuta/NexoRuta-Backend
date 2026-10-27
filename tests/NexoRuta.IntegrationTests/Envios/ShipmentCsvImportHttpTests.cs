using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexoRuta.Application.Envios.Importacion;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Outbox;
using Xunit.Abstractions;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class ShipmentCsvImportHttpTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>
{
    private const string Header = "referenciaExterna,nombreDestinatario,emailDestinatario,direccion,codigoBulto,pesoGramos,largoCentimetros,anchoCentimetros,altoCentimetros";

    [Fact]
    public async Task ImportLockFunctionOrdersDistinctEffectiveHashKeysGlobally()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_get_functiondef('nexoruta_app.lock_import_references(text[])'::regprocedure)";
        var definition = (string)(await command.ExecuteScalarAsync())!;

        Assert.Contains("SELECT DISTINCT pg_catalog.hashtextextended", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY lock_key", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_advisory_xact_lock(v_lock_key)", definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_CreatesOnceAndRecognizesCompleteAndSubsetReimports()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        await client.SetBearerForAccessAsync(database, commerceAccessId);
        var csv = Csv(
            Row("PED-1", weight: "1200.555", length: "30.007"),
            Row("PED-2", address: "\"Calle 1, apto 2\""));

        using var first = await ImportAsync(client, csv, operatorId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await Body(first);
        Assert.Equal(2, Int(firstBody, "resumen", "creados"));
        Assert.Equal(0, Int(firstBody, "resumen", "existentes"));
        Assert.Equal(new[] { "Creado", "Creado" }, States(firstBody));
        var createdIds = firstBody.GetProperty("filas").EnumerateArray()
            .Select(row => row.GetProperty("envioId").GetGuid()).ToArray();
        var createdShipments = await db.Envios.Where(x => createdIds.Contains(x.Id)).ToArrayAsync();
        Assert.Equal(2, createdShipments.Length);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"EnvioEventos\" WHERE \"Tipo\" = 'Creacion'").SingleAsync());
        var ruleVersionIds = await db.Set<VersionReglasOperativas>().Where(x => x.OperadorId == operatorId)
            .Select(x => x.Id).ToArrayAsync();
        Assert.All(createdShipments, shipment => Assert.Contains(shipment.VersionReglasId, ruleVersionIds));

        using var completeRetry = await ImportAsync(client, csv, operatorId);
        Assert.Equal(HttpStatusCode.OK, completeRetry.StatusCode);
        var completeBody = await Body(completeRetry);
        Assert.Equal(0, Int(completeBody, "resumen", "creados"));
        Assert.Equal(2, Int(completeBody, "resumen", "existentes"));
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"EnvioEventos\" WHERE \"Tipo\" = 'Creacion'").SingleAsync());

        using var subsetRetry = await ImportAsync(client, Csv(Row("PED-2", address: "\"Calle 1, apto 2\"")), operatorId);
        Assert.Equal(HttpStatusCode.OK, subsetRetry.StatusCode);
        Assert.Equal(1, Int(await Body(subsetRetry), "resumen", "existentes"));
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"EnvioEventos\" WHERE \"Tipo\" = 'Creacion'").SingleAsync());

        using var mixed = await ImportAsync(client,
            Csv(Row("PED-2", address: "\"Calle 1, apto 2\""), Row("PED-3")), operatorId);
        Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
        var mixedBody = await Body(mixed);
        Assert.Equal(1, Int(mixedBody, "resumen", "creados"));
        Assert.Equal(1, Int(mixedBody, "resumen", "existentes"));
        Assert.Equal(new[] { "YaExistente", "Creado" }, States(mixedBody));
        Assert.Equal(3, await db.Envios.CountAsync());
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"EnvioEventos\" WHERE \"Tipo\" = 'Creacion'").SingleAsync());
        Assert.Equal(3, await db.IdentidadesEnvioImportado.CountAsync());
        Assert.Equal(6, await db.OutboxMessages.CountAsync());
        Assert.Equal(3, await db.OutboxMessages.CountAsync(x => x.Tipo == OutboxMessageTypes.TrackingRequested && x.Destino == OutboxDestino.RabbitMq));
        Assert.Equal(3, await db.OutboxMessages.CountAsync(x => x.Tipo == OutboxMessageTypes.ShipmentCreated && x.Destino == OutboxDestino.Kafka));
    }

    [Fact]
    public async Task Import_CoalescesEquivalentInternalReferencesAndRejectsIncompatibleOnes()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        await client.SetBearerForAccessAsync(database, commerceAccessId);

        using var duplicate = await ImportAsync(client, Csv(Row("SAME", weight: "10.0"), Row("SAME", weight: "10.00")), operatorId);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var duplicateBody = await Body(duplicate);
        Assert.Equal(1, Int(duplicateBody, "resumen", "creados"));
        Assert.Equal(1, Int(duplicateBody, "resumen", "duplicadosEnArchivo"));
        var repeated = duplicateBody.GetProperty("filas")[1];
        Assert.Equal("DuplicadoDe", repeated.GetProperty("estado").GetString());
        Assert.Equal(1, repeated.GetProperty("mismoQueRegistro").GetInt32());

        using var conflict = await ImportAsync(client, Csv(Row("OTHER", weight: "1"), Row("OTHER", name: "Different recipient", weight: "1"), Row("NEW-LOCAL")), operatorId);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var conflictBody = await Body(conflict);
        Assert.Equal(0, Int(conflictBody, "resumen", "creados"));
        Assert.Equal(2, Int(conflictBody, "resumen", "conflictos"));
        Assert.Equal(1, Int(conflictBody, "resumen", "noProcesados"));
        Assert.Equal(1, await db.Envios.CountAsync());
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Import_RejectsInvalidRowsAtomicallyAndDistinguishesExternalConflict()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        await client.SetBearerForAccessAsync(database, commerceAccessId);
        using var first = await ImportAsync(client, Csv(Row("EXISTS")), operatorId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var beforeOutbox = await db.OutboxMessages.CountAsync();

        using var externalConflict = await ImportAsync(client,
            Csv(Row("NEW-1"), Row("EXISTS", name: "Changed original recipient")), operatorId);
        Assert.Equal(HttpStatusCode.Conflict, externalConflict.StatusCode);
        var conflictBody = await Body(externalConflict);
        Assert.Equal(0, Int(conflictBody, "resumen", "creados"));
        Assert.Equal(1, Int(conflictBody, "resumen", "conflictos"));
        Assert.Equal(1, Int(conflictBody, "resumen", "noProcesados"));
        Assert.Equal(1, await db.Envios.CountAsync());
        Assert.Equal(beforeOutbox, await db.OutboxMessages.CountAsync());

        using var invalid = await ImportAsync(client,
            Csv(Row("NEW-2"), "BAD,Row,bad-email,Address,BOX,1e2,1,1,1"), operatorId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var invalidBody = await Body(invalid);
        Assert.Equal(0, Int(invalidBody, "resumen", "creados"));
        Assert.Equal(1, Int(invalidBody, "resumen", "invalidos"));
        Assert.Equal(1, Int(invalidBody, "resumen", "noProcesados"));
        Assert.Contains(invalidBody.GetProperty("filas")[1].GetProperty("errores").EnumerateArray(),
            error => error.GetProperty("campo").GetString() == "pesoGramos");
        Assert.Equal(1, await db.Envios.CountAsync());
        Assert.Equal(beforeOutbox, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Import_IsCommerceScopedAndOperatorIsPartOfEquivalenceButNotIdentity()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var commerceOne = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorOne, accessOne) = await GetCommerceAccessAsync(db);
        await commerceOne.SetBearerForAccessAsync(database, accessOne);

        const string secondCommerceEmail = "csv-second-commerce@example.test";
        var firstCommerceId = await db.AccesosUsuario.Where(x => x.Id == accessOne).Select(x => x.ComercioId).SingleAsync();
        await AuthenticationTestData.Seeder(db, new AccesoInicial(secondCommerceEmail, "Second operator", "Second commerce", "csv-second-operator@example.test"))
            .SeedAsync();
        await ReglasEnvioTestData.SembrarInicialesAsync(db);
        var secondCommerceId = await db.Comercios.Where(x => x.Nombre == "Second commerce").Select(x => x.Id).SingleAsync();
        var secondOperatorId = await db.Operadores.Where(x => x.Nombre == "Second operator").Select(x => x.Id).SingleAsync();
        var accessTwo = await db.AccesosUsuario.Where(x => x.ComercioId == secondCommerceId).Select(x => x.Id).SingleAsync();
        using var commerceTwo = factory.CreateHttpClient();
        await commerceTwo.SetBearerForAccessAsync(database, accessTwo);

        using var importOne = await ImportAsync(commerceOne, Csv(Row("SAME-TENANT-KEY")), operatorOne);
        Assert.Equal(HttpStatusCode.OK, importOne.StatusCode);
        using var importTwo = await ImportAsync(commerceTwo, Csv(Row("SAME-TENANT-KEY")), secondOperatorId,
            extraCommerceId: firstCommerceId);
        Assert.Equal(HttpStatusCode.OK, importTwo.StatusCode);
        Assert.Equal(1, Int(await Body(importTwo), "resumen", "creados"));

        using var operatorConflict = await ImportAsync(commerceOne, Csv(Row("SAME-TENANT-KEY")), secondOperatorId);
        Assert.Equal(HttpStatusCode.Conflict, operatorConflict.StatusCode);
        Assert.Equal(2, await db.Envios.CountAsync());
        Assert.Equal(1, await db.Envios.CountAsync(x => x.ComercioId == secondCommerceId));
        Assert.Equal(4, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Import_ExistingIdentitySurvivesInactiveOperatorCancellationAndLatePhysicalReconciliation()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        await client.SetBearerForAccessAsync(database, commerceAccessId);
        var csv = Csv(Row("TERMINAL", weight: "100.125"));
        using var created = await ImportAsync(client, csv, operatorId);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdBody = await Body(created);
        var shipmentId = createdBody.GetProperty("filas")[0].GetProperty("envioId").GetGuid();
        using var list = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var detail = Assert.Single(await list.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        var version = detail.GetProperty("version").GetInt64();
        using var cancelled = await client.PostAsJsonAsync($"/api/envios/{shipmentId}/cancelacion", new { Version = version, Motivo = "integration test" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var shipment = await db.Envios.SingleAsync(x => x.Id == shipmentId);
        Assert.Equal(EstadoEnvio.Cancelado, shipment.Estado);
        var recibidoEn = DateTimeOffset.UtcNow;
        shipment.ConciliarEntregaFisica(DateTimeOffset.UtcNow.AddMinutes(-1), recibidoEn,
            plazoValidacionMinutos: null, pruebaCompleta: false, versionEsperada: shipment.Version);
        var address = await db.Direcciones.SingleAsync(x => x.Id == shipment.DireccionId);
        address.CambiarDescripcion("Dirección interna actualizada tras el alta", address.Version);
        await db.SaveChangesAsync();
        Assert.Equal(EstadoEnvio.Cancelado, (await db.Envios.SingleAsync(x => x.Id == shipmentId)).Estado);
        var outboxAfterCancellationAndLateFact = await db.OutboxMessages.CountAsync();

        using var replay = await ImportAsync(client, csv, operatorId);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await Body(replay);
        Assert.Equal("YaExistente", replayBody.GetProperty("filas")[0].GetProperty("estado").GetString());
        Assert.Equal(0, Int(replayBody, "resumen", "creados"));
        Assert.Equal(EstadoEnvio.Cancelado, (await db.Envios.SingleAsync(x => x.Id == shipmentId)).Estado);
        Assert.Equal(outboxAfterCancellationAndLateFact, await db.OutboxMessages.CountAsync());

        var op = await db.Operadores.SingleAsync(x => x.Id == operatorId);
        op.CambiarEstado(EstadoOrganizacion.Suspendida);
        await db.SaveChangesAsync();
        using var inactiveRetry = await ImportAsync(client, csv, operatorId);
        Assert.Equal(HttpStatusCode.OK, inactiveRetry.StatusCode);
        Assert.Equal(1, Int(await Body(inactiveRetry), "resumen", "existentes"));
        using var inactiveNew = await ImportAsync(client, Csv(Row("NEW-INACTIVE")), operatorId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactiveNew.StatusCode);
        using var inactiveMixed = await ImportAsync(client, Csv(Row("TERMINAL", weight: "100.125"), Row("NEW-INACTIVE-MIXED")), operatorId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactiveMixed.StatusCode);
        var inactiveMixedBody = await Body(inactiveMixed);
        Assert.Equal(new[] { "YaExistente", "NoProcesado" }, States(inactiveMixedBody));
        Assert.Equal(1, await db.Envios.CountAsync());
        Assert.Equal(outboxAfterCancellationAndLateFact, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Import_ConcurrentOverlappingReferencesInReverseOrderSerializesWithoutDuplicates()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        await client.SetBearerForAccessAsync(database, commerceAccessId);
        var forward = Csv(Row("LOCK-A"), Row("LOCK-B"));
        var reverse = Csv(Row("LOCK-B"), Row("LOCK-A"));
        var watch = Stopwatch.StartNew();
        var firstTask = ImportAsync(client, forward, operatorId);
        var secondTask = ImportAsync(client, reverse, operatorId);
        using var first = await firstTask;
        using var second = await secondTask;
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await Body(first);
        var secondBody = await Body(second);
        Assert.Equal(2, Int(firstBody, "resumen", "creados") + Int(secondBody, "resumen", "creados"));
        Assert.Equal(2, Int(firstBody, "resumen", "existentes") + Int(secondBody, "resumen", "existentes"));
        Assert.Equal(2, await db.Envios.CountAsync());
        Assert.Equal(4, await db.OutboxMessages.CountAsync());
        output.WriteLine($"Two overlapping reverse-order imports completed in {watch.Elapsed.TotalMilliseconds:F0} ms; created={Int(firstBody, "resumen", "creados") + Int(secondBody, "resumen", "creados")}, existing={Int(firstBody, "resumen", "existentes") + Int(secondBody, "resumen", "existentes")}.");

        var conflictingA = ImportAsync(client, Csv(Row("LOCK-CONFLICT", name: "Recipient A")), operatorId);
        var conflictingB = ImportAsync(client, Csv(Row("LOCK-CONFLICT", name: "Recipient B")), operatorId);
        using var conflictA = await conflictingA;
        using var conflictB = await conflictingB;
        Assert.Contains(new[] { conflictA.StatusCode, conflictB.StatusCode }, status => status == HttpStatusCode.OK);
        Assert.Contains(new[] { conflictA.StatusCode, conflictB.StatusCode }, status => status == HttpStatusCode.Conflict);
        Assert.Equal(3, await db.Envios.CountAsync());
        Assert.Equal(6, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Import_RetryRecoversAfterCommitCompletedButResponseWasLost()
    {
        var database = await postgres.CreateDatabaseAsync();
        var interceptor = new PostCommitFailureInterceptor();
        using var factory = database.CreateApiFactory(interceptor);
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        await client.SetBearerForAccessAsync(database, commerceAccessId);
        var csv = Csv(Row("COMMIT-UNCERTAIN"));
        interceptor.FailNextCommit();

        var lost = await Record.ExceptionAsync(async () =>
        {
            using var response = await ImportAsync(client, csv, operatorId);
        });
        Assert.NotNull(lost);
        Assert.Equal(1, await db.Envios.CountAsync());
        Assert.Equal(1, await db.IdentidadesEnvioImportado.CountAsync());
        Assert.Equal(2, await db.OutboxMessages.CountAsync());

        using var retry = await ImportAsync(client, csv, operatorId);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryBody = await Body(retry);
        Assert.Equal(0, Int(retryBody, "resumen", "creados"));
        Assert.Equal(1, Int(retryBody, "resumen", "existentes"));
        Assert.Equal(1, await db.Envios.CountAsync());
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Import_ProcessesFiveHundredRowsAndReportsElapsedTime()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        await client.SetBearerForAccessAsync(database, commerceAccessId);
        var csv = Csv(Enumerable.Range(1, 500).Select(i => Row($"BULK-{i:D4}")).ToArray());
        var watch = Stopwatch.StartNew();
        using var response = await ImportAsync(client, csv, operatorId);
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(500, Int(await Body(response), "resumen", "creados"));
        Assert.Equal(500, await db.Envios.CountAsync());
        Assert.Equal(1000, await db.OutboxMessages.CountAsync());
        output.WriteLine($"500-row HTTP import: {watch.Elapsed.TotalMilliseconds:F0} ms in Docker Testcontainers PostgreSQL (postgis/postgis:18-3.6-alpine), single local test process.");
    }

    [Fact]
    public async Task OpenApi_DescribesImportAsMultipartFormData()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, environment: "Development");
        using var client = factory.CreateHttpClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var openApi = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/envios/importaciones", openApi, StringComparison.Ordinal);
        Assert.Contains("/api/operadores/importacion-envios", openApi, StringComparison.Ordinal);
        Assert.Contains("multipart/form-data", openApi, StringComparison.Ordinal);
        Assert.Contains("archivo", openApi, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operadorId", openApi, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/envios/{id}/iniciar-devolucion", openApi, StringComparison.Ordinal);
        Assert.Contains("IniciarDevolucionEnvioRequest", openApi, StringComparison.Ordinal);
        Assert.Contains("DecisionDevolucionEnvioResponse", openApi, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ImportFailPoint.AfterFirstShipment)]
    [InlineData(ImportFailPoint.IdentityBinding)]
    [InlineData(ImportFailPoint.OutboxInsert)]
    public async Task Import_FailureInsideBatchRollsBackShipmentIdentityAndOutbox(ImportFailPoint failPoint)
    {
        var database = await postgres.CreateDatabaseAsync();
        var interceptor = new FailImportInterceptor(failPoint);
        using var factory = database.CreateApiFactory(interceptor);
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        await client.SetBearerForAccessAsync(database, commerceAccessId);

        var failure = await Record.ExceptionAsync(
            () => ImportAsync(client, Csv(Row("ROLLBACK-1"), Row("ROLLBACK-2")), operatorId));
        Assert.NotNull(failure);
        Assert.Contains("Injected", failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(await db.Envios.ToArrayAsync());
        Assert.Empty(await db.Destinatarios.ToArrayAsync());
        Assert.Empty(await db.Direcciones.ToArrayAsync());
        Assert.Empty(await db.Bultos.ToArrayAsync());
        Assert.Empty(await db.IdentidadesEnvioImportado.ToArrayAsync());
        Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }

    [Fact]
    public async Task Import_RejectsOperatorActorAndMalformedFileWithoutBusinessWrites()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (operatorId, commerceAccessId) = await GetCommerceAccessAsync(db);
        var operatorAccess = await db.AccesosUsuario.Where(x => x.OperadorId == operatorId).Select(x => x.Id).SingleAsync();
        await client.SetBearerForAccessAsync(database, operatorAccess);
        using var denied = await ImportAsync(client, Csv(Row("DENIED")), operatorId);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await client.SetBearerForAccessAsync(database, commerceAccessId);
        using var malformed = await ImportAsync(client, Header.Replace("codigoBulto", "codigo", StringComparison.Ordinal) + "\n" + Row("BAD-HEADER"), operatorId);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var tooMany = await ImportAsync(client,
            Csv(Enumerable.Range(1, 501).Select(i => Row($"TOO-MANY-{i}")).ToArray()), operatorId);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMany.StatusCode);
        using var tooLarge = await ImportAsync(client, new byte[CsvEnviosParser.MaximumFileBytes + 1], operatorId);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Empty(await db.Envios.ToArrayAsync());
        Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }

    private static async Task<(Guid OperatorId, Guid CommerceAccessId)> GetCommerceAccessAsync(NexoRuta.Infrastructure.Persistence.NexoRutaDbContext db)
    {
        var operatorId = await db.Operadores.Select(x => x.Id).SingleAsync();
        var accessId = await db.AccesosUsuario.Where(x => x.ComercioId != null).Select(x => x.Id).SingleAsync();
        return (operatorId, accessId);
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string csv, Guid operatorId, Guid? extraCommerceId = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "archivo", "envios.csv");
        form.Add(new StringContent(operatorId.ToString("D")), "operadorId");
        if (extraCommerceId is not null)
            form.Add(new StringContent(extraCommerceId.Value.ToString("D")), "comercioId");
        return client.PostAsync("/api/envios/importaciones", form);
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, byte[] bytes, Guid operatorId)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "archivo", "envios.csv");
        form.Add(new StringContent(operatorId.ToString("D")), "operadorId");
        return client.PostAsync("/api/envios/importaciones", form);
    }

    private static string Csv(params string[] rows) => Header + "\r\n" + string.Join("\r\n", rows);

    private static string Row(string reference, string name = "Ada Lovelace", string address = "18 de Julio 123",
        string weight = "500", string length = "20")
        => $"{reference},{name},ada@example.test,{address},BOX-{reference},{weight},{length},10,5";

    private static async Task<JsonElement> Body(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static int Int(JsonElement body, string parent, string field)
        => body.GetProperty(parent).GetProperty(field).GetInt32();

    private static string[] States(JsonElement body)
        => body.GetProperty("filas").EnumerateArray().Select(x => x.GetProperty("estado").GetString()!).ToArray();

    public enum ImportFailPoint { AfterFirstShipment, IdentityBinding, OutboxInsert }

    private sealed class FailImportInterceptor(ImportFailPoint failPoint) : DbCommandInterceptor
    {
        private int shipmentCreates;
        private int identityBindings;
        private bool failed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (failPoint == ImportFailPoint.AfterFirstShipment
                && command.CommandText.Contains("nexoruta_app.create_shipment", StringComparison.Ordinal)
                && Interlocked.Increment(ref shipmentCreates) == 2)
                throw new InvalidOperationException("Injected failure after the first shipment insert.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            var executed = await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
            if (failPoint == ImportFailPoint.OutboxInsert
                && command.CommandText.Contains("OutboxMessages", StringComparison.Ordinal)
                && !failed)
            {
                failed = true;
                await executed.DisposeAsync();
                throw new InvalidOperationException("Injected failure after outbox insertion.");
            }
            return executed;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var match = failPoint switch
            {
                ImportFailPoint.IdentityBinding => command.CommandText.Contains("bind_import_identity", StringComparison.Ordinal)
                    && Interlocked.Increment(ref identityBindings) == 2,
                _ => false
            };
            if (match && !failed)
            {
                failed = true;
                throw new InvalidOperationException("Injected batch persistence failure.");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class PostCommitFailureInterceptor : DbTransactionInterceptor
    {
        private int failNextCommit;

        public void FailNextCommit() => Interlocked.Exchange(ref failNextCommit, 1);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref failNextCommit, 0) == 1)
                throw new IOException("Injected response loss after PostgreSQL committed the transaction.");
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }
}
