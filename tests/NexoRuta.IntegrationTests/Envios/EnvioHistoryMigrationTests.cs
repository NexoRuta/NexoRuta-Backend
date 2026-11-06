using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
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
            // Seed only columns that existed at the cutover predecessor. Never
            // synthesize future columns or forge the EF migration history.
            await AuthenticationTestData.Seeder(db,
                new AccesoInicial(commerceEmail, "History operator", "History commerce", "history-operator@example.test"))
                .SeedAsync();
            var access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
            operatorId = await db.Operadores.Select(x => x.Id).SingleAsync();
            commerceId = access.ComercioId!.Value;
            userId = access.UsuarioId;
            accessId = access.Id;
            var ruleVersionId = Guid.NewGuid();
            await using var rulesTransaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."ReglasOperativasOperador" ("OperadorId","ActualizadoUtc") VALUES ({operatorId},now());
                INSERT INTO public."VersionesReglasOperativas"
                    ("OperadorId","Id","Estado","NumeroVersionPublicada","MaxIntentosEntrega",
                     "IntervaloEntreIntentosMinutos","PoliticaDevolucionAlAgotarIntentos",
                     "PlazoEstandarMinutos","PlazoUrgenteMinutos","CreadaUtc","CreadaPorUsuarioId",
                     "PublicadaUtc","PublicadaPorUsuarioId")
                VALUES ({operatorId},{ruleVersionId},'Borrador',NULL,3,1,'DecisionOperativa',2880,1440,
                    now(),{userId},NULL,NULL);
                INSERT INTO public."RequisitosPruebaEntrega" ("OperadorId","VersionId","Tipo")
                    VALUES ({operatorId},{ruleVersionId},'Fotografia');
                INSERT INTO public."MotivosNoEntregaVersion" ("OperadorId","VersionId","Codigo","Descripcion","Disponible")
                    VALUES ({operatorId},{ruleVersionId},'AUSENTE','Ausente',true);
                UPDATE public."VersionesReglasOperativas" SET "Estado"='Publicada',"NumeroVersionPublicada"=1,
                    "PublicadaUtc"=now(),"PublicadaPorUsuarioId"={userId}
                    WHERE "OperadorId"={operatorId} AND "Id"={ruleVersionId};
                UPDATE public."ReglasOperativasOperador" SET "VersionVigenteId"={ruleVersionId},
                    "UltimoNumeroPublicacion"=1,"ActualizadoUtc"=now() WHERE "OperadorId"={operatorId};
                """);

            await rulesTransaction.CommitAsync();
            var shipments = new List<Envio>();
            for (var i = 0; i < shipmentCount; i++)
            {
                var recipient = new Destinatario(operatorId, $"Recipient {i}", $"recipient-{i}@example.test");
                var address = new Direccion(operatorId, $"History address {i}");
                var shipment = new Envio(operatorId, commerceId, userId, recipient.Id, address.Id, ruleVersionId);
                var parcel = new Bulto(operatorId, shipment.Id, $"HIST-{i:D3}", 500m, 20m, 10m, 5m);
                db.AddRange(recipient, address);
                await db.SaveChangesAsync();
                // Use the exact historical shipment columns.
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.\"Envios\" (\"Id\",\"OperadorId\",\"ComercioId\",\"CreadoPorUsuarioId\",\"DestinatarioId\",\"DireccionId\",\"VersionReglasId\",\"Estado\") VALUES ({shipment.Id},{operatorId},{commerceId},{userId},{recipient.Id},{address.Id},{ruleVersionId},'Admitido')");
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO public."Bultos" ("Id","OperadorId","EnvioId","Codigo","PesoGramos",
                        "LargoCentimetros","AnchoCentimetros","AltoCentimetros")
                    VALUES ({parcel.Id},{operatorId},{shipment.Id},{parcel.Codigo},500,20,10,5)
                    """);
                shipments.Add(shipment);
            }
            await db.SaveChangesAsync();
            canceledShipmentId = shipments[0].Id;
        }

        // Invoke the real historical cancellation function with the authenticated
        // commerce scope. The current API deliberately targets the current schema.
        await using (var db = database.CreateDbContext())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('nexoruta.actor_kind','comercio',true),
                    set_config('nexoruta.user_id',{userId.ToString()},true),
                    set_config('nexoruta.access_id',{accessId.ToString()},true),
                    set_config('nexoruta.org_id',{commerceId.ToString()},true)
                """);
            var auditId = await db.Database.SqlQuery<Guid>($"""
                SELECT nexoruta_app.cancel_shipment({canceledShipmentId},0::bigint,
                    'Audit-backed cutover fixture.',{Guid.NewGuid()},{Guid.NewGuid()},{Guid.NewGuid()}) AS "Value"
                """).SingleAsync();
            Assert.NotEqual(Guid.Empty, auditId);
            await tx.CommitAsync();
        }

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
        {
            await db.GetService<IMigrator>().MigrateAsync();
        }
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

}
