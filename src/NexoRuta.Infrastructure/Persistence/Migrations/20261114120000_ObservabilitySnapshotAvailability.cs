using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261114120000_ObservabilitySnapshotAvailability")]
public sealed class ObservabilitySnapshotAvailability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP FUNCTION nexoruta_app.observability_snapshot();
        CREATE FUNCTION nexoruta_app.observability_snapshot()
        RETURNS TABLE(
            "PendingSyncOperations" bigint,
            "FreshMobileInstallations" bigint,
            "StaleMobileInstallations" bigint,
            "OldestMobileSyncReportSeconds" double precision,
            "OldestPendingWebhookSeconds" double precision,
            "PendingWebhooks" bigint,
            "RetryWebhooks" bigint,
            "DeliveredWebhooks" bigint,
            "FailedWebhooks" bigint,
            "SnapshotAvailable" boolean)
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
            WITH mobile AS (
                SELECT "PendingOperations", "ReportedAtUtc"
                FROM public."MobileSyncSnapshots"
            ), hooks AS (
                SELECT "Estado", "CreadaEnUtc"
                FROM public."WebhookDeliveries"
            )
            SELECT
                coalesce((SELECT sum("PendingOperations") FROM mobile WHERE "ReportedAtUtc">clock_timestamp()-interval '5 minutes'),0)::bigint,
                (SELECT count(*) FROM mobile WHERE "ReportedAtUtc">clock_timestamp()-interval '5 minutes')::bigint,
                (SELECT count(*) FROM mobile WHERE "ReportedAtUtc"<=clock_timestamp()-interval '5 minutes')::bigint,
                coalesce((SELECT extract(epoch FROM clock_timestamp()-min("ReportedAtUtc")) FROM mobile),0)::double precision,
                coalesce((SELECT extract(epoch FROM clock_timestamp()-min("CreadaEnUtc"))
                    FROM hooks WHERE "Estado" IN('Pendiente','Reintento','Procesando')),0)::double precision,
                (SELECT count(*) FROM hooks WHERE "Estado"='Pendiente')::bigint,
                (SELECT count(*) FROM hooks WHERE "Estado" IN('Reintento','Procesando'))::bigint,
                (SELECT count(*) FROM hooks WHERE "Estado"='Entregado')::bigint,
                (SELECT count(*) FROM hooks WHERE "Estado"='Fallido')::bigint,
                true
        $$;
        ALTER FUNCTION nexoruta_app.observability_snapshot() OWNER TO nexoruta_webhook_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.observability_snapshot() FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION nexoruta_app.observability_snapshot() TO nexoruta_worker;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "The observability snapshot contract is retained; do not remove snapshot state or availability telemetry.");
}
