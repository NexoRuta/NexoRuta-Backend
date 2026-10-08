using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperatorUserAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "OperadorComercioId",
                table: "AccesosUsuario",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_UsuarioId_OperadorId",
                table: "AccesosUsuario",
                columns: new[] { "UsuarioId", "OperadorId" },
                unique: true,
                filter: "\"OperadorComercioId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_UsuarioId_OperadorId",
                table: "AccesosUsuario");

            migrationBuilder.AlterColumn<Guid>(
                name: "OperadorComercioId",
                table: "AccesosUsuario",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
