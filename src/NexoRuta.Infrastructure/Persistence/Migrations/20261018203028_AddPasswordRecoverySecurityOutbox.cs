using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations;

/// <summary>Retains the historical Identity stamp backfill; recovery messages now use OutboxMessages.</summary>
public partial class AddPasswordRecoverySecurityOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("UPDATE public.\"Usuarios\" SET \"SecurityStamp\" = gen_random_uuid()::text WHERE \"SecurityStamp\" IS NULL;");

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
