using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations;

/// <summary>
/// Retained in the historical chain; the original outbox migration now creates its final lease columns directly.
/// </summary>
public partial class AddOutboxProcessingLease : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
