using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotParcelsPerVisit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "BultoIds",
                table: "ParadasRuta",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);
            migrationBuilder.Sql("""
                UPDATE public."ParadasRuta" p SET "BultoIds"=coalesce((SELECT array_agg(b."Id" ORDER BY b."Id") FROM public."Bultos" b WHERE b."OperadorId"=p."OperadorId" AND b."EnvioId"=p."EnvioId"),ARRAY[]::uuid[]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BultoIds",
                table: "ParadasRuta");
        }
    }
}
