using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCourierOperationsAndRemoteNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SalidaEnUtc",
                table: "HojasRuta",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SesionesMoviles_Id_AccesoId",
                table: "SesionesMoviles",
                columns: new[] { "Id", "AccesoId" });

            migrationBuilder.CreateTable(
                name: "AvisosReparto",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    RutaId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionEnvio = table.Column<long>(type: "bigint", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecibidoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AbiertoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvisosReparto", x => x.Id);
                    table.UniqueConstraint("AK_AvisosReparto_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.ForeignKey(
                        name: "FK_AvisosReparto_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AvisosReparto_HojasRuta_OperadorId_RutaId",
                        columns: x => new { x.OperadorId, x.RutaId },
                        principalTable: "HojasRuta",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AvisosReparto_Repartidores_AccesoId_OperadorId",
                        columns: x => new { x.AccesoId, x.OperadorId },
                        principalTable: "Repartidores",
                        principalColumns: new[] { "AccesoId", "OperadorId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenciasReparto",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    RutaId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Cifrado = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    RecibidaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenciasReparto", x => x.Id);
                    table.UniqueConstraint("AK_EvidenciasReparto_OperadorId_EnvioId_Id", x => new { x.OperadorId, x.EnvioId, x.Id });
                    table.ForeignKey(
                        name: "FK_EvidenciasReparto_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenciasReparto_HojasRuta_OperadorId_RutaId",
                        columns: x => new { x.OperadorId, x.RutaId },
                        principalTable: "HojasRuta",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenciasReparto_Repartidores_AccesoId_OperadorId",
                        columns: x => new { x.AccesoId, x.OperadorId },
                        principalTable: "Repartidores",
                        principalColumns: new[] { "AccesoId", "OperadorId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InstalacionesMoviles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    SesionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Plataforma = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FidHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FidCifrado = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    Permitida = table.Column<bool>(type: "boolean", nullable: false),
                    Activa = table.Column<bool>(type: "boolean", nullable: false),
                    ActualizadaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstalacionesMoviles", x => x.Id);
                    table.UniqueConstraint("AK_InstalacionesMoviles_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.ForeignKey(
                        name: "FK_InstalacionesMoviles_Repartidores_AccesoId_OperadorId",
                        columns: x => new { x.AccesoId, x.OperadorId },
                        principalTable: "Repartidores",
                        principalColumns: new[] { "AccesoId", "OperadorId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstalacionesMoviles_SesionesMoviles_SesionId_AccesoId",
                        columns: x => new { x.SesionId, x.AccesoId },
                        principalTable: "SesionesMoviles",
                        principalColumns: new[] { "Id", "AccesoId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperacionesJornada",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    RutaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Secuencia = table.Column<long>(type: "bigint", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: true),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Respuesta = table.Column<string>(type: "jsonb", nullable: false),
                    Resultado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RecibidaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperacionesJornada", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperacionesJornada_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperacionesJornada_HojasRuta_OperadorId_RutaId",
                        columns: x => new { x.OperadorId, x.RutaId },
                        principalTable: "HojasRuta",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperacionesJornada_Repartidores_AccesoId_OperadorId",
                        columns: x => new { x.AccesoId, x.OperadorId },
                        principalTable: "Repartidores",
                        principalColumns: new[] { "AccesoId", "OperadorId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PruebasEntrega",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RutaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Documento = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FotoId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirmaId = table.Column<Guid>(type: "uuid", nullable: true),
                    Latitud = table.Column<double>(type: "double precision", nullable: true),
                    Longitud = table.Column<double>(type: "double precision", nullable: true),
                    PrecisionMetros = table.Column<double>(type: "double precision", nullable: true),
                    PosicionEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PruebasEntrega", x => new { x.OperadorId, x.EnvioId });
                    table.ForeignKey(
                        name: "FK_PruebasEntrega_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PruebasEntrega_EvidenciasReparto_OperadorId_EnvioId_FirmaId",
                        columns: x => new { x.OperadorId, x.EnvioId, x.FirmaId },
                        principalTable: "EvidenciasReparto",
                        principalColumns: new[] { "OperadorId", "EnvioId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PruebasEntrega_EvidenciasReparto_OperadorId_EnvioId_FotoId",
                        columns: x => new { x.OperadorId, x.EnvioId, x.FotoId },
                        principalTable: "EvidenciasReparto",
                        principalColumns: new[] { "OperadorId", "EnvioId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PruebasEntrega_HojasRuta_OperadorId_RutaId",
                        columns: x => new { x.OperadorId, x.RutaId },
                        principalTable: "HojasRuta",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EntregasPush",
                columns: table => new
                {
                    AvisoId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstalacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FidHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Resultado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IdFcm = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RegistradaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntregasPush", x => new { x.AvisoId, x.InstalacionId, x.FidHash });
                    table.ForeignKey(
                        name: "FK_EntregasPush_AvisosReparto_OperadorId_AvisoId",
                        columns: x => new { x.OperadorId, x.AvisoId },
                        principalTable: "AvisosReparto",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EntregasPush_InstalacionesMoviles_OperadorId_InstalacionId",
                        columns: x => new { x.OperadorId, x.InstalacionId },
                        principalTable: "InstalacionesMoviles",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvisosReparto_AccesoId_OperadorId",
                table: "AvisosReparto",
                columns: new[] { "AccesoId", "OperadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_AvisosReparto_OperadorId_AccesoId_CreadoEnUtc",
                table: "AvisosReparto",
                columns: new[] { "OperadorId", "AccesoId", "CreadoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AvisosReparto_OperadorId_EnvioId",
                table: "AvisosReparto",
                columns: new[] { "OperadorId", "EnvioId" });

            migrationBuilder.CreateIndex(
                name: "IX_AvisosReparto_OperadorId_RutaId",
                table: "AvisosReparto",
                columns: new[] { "OperadorId", "RutaId" });

            migrationBuilder.CreateIndex(
                name: "IX_EntregasPush_OperadorId_AvisoId",
                table: "EntregasPush",
                columns: new[] { "OperadorId", "AvisoId" });

            migrationBuilder.CreateIndex(
                name: "IX_EntregasPush_OperadorId_InstalacionId",
                table: "EntregasPush",
                columns: new[] { "OperadorId", "InstalacionId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenciasReparto_AccesoId_OperadorId",
                table: "EvidenciasReparto",
                columns: new[] { "AccesoId", "OperadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenciasReparto_OperadorId_RutaId",
                table: "EvidenciasReparto",
                columns: new[] { "OperadorId", "RutaId" });

            migrationBuilder.CreateIndex(
                name: "IX_InstalacionesMoviles_AccesoId_OperadorId",
                table: "InstalacionesMoviles",
                columns: new[] { "AccesoId", "OperadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_InstalacionesMoviles_OperadorId_AccesoId_FidHash",
                table: "InstalacionesMoviles",
                columns: new[] { "OperadorId", "AccesoId", "FidHash" });

            migrationBuilder.CreateIndex(
                name: "IX_InstalacionesMoviles_SesionId_AccesoId",
                table: "InstalacionesMoviles",
                columns: new[] { "SesionId", "AccesoId" });

            migrationBuilder.CreateIndex(
                name: "IX_OperacionesJornada_AccesoId_OperadorId",
                table: "OperacionesJornada",
                columns: new[] { "AccesoId", "OperadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_OperacionesJornada_OperadorId_EnvioId",
                table: "OperacionesJornada",
                columns: new[] { "OperadorId", "EnvioId" });

            migrationBuilder.CreateIndex(
                name: "IX_OperacionesJornada_OperadorId_RutaId_Secuencia",
                table: "OperacionesJornada",
                columns: new[] { "OperadorId", "RutaId", "Secuencia" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PruebasEntrega_OperadorId_EnvioId_FirmaId",
                table: "PruebasEntrega",
                columns: new[] { "OperadorId", "EnvioId", "FirmaId" });

            migrationBuilder.CreateIndex(
                name: "IX_PruebasEntrega_OperadorId_EnvioId_FotoId",
                table: "PruebasEntrega",
                columns: new[] { "OperadorId", "EnvioId", "FotoId" });

            migrationBuilder.CreateIndex(
                name: "IX_PruebasEntrega_OperadorId_RutaId",
                table: "PruebasEntrega",
                columns: new[] { "OperadorId", "RutaId" });
            foreach (var tabla in new[] { "OperacionesJornada", "EvidenciasReparto", "PruebasEntrega", "AvisosReparto", "InstalacionesMoviles", "EntregasPush" })
                migrationBuilder.Sql($"""
                    ALTER TABLE "{tabla}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{tabla}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY operador_reparto ON "{tabla}"
                    USING ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid)
                    WITH CHECK ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid);
                    """);
            migrationBuilder.Sql("""
                CREATE FUNCTION "ImpedirCambioHechoReparto"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Los hechos y las evidencias de reparto son inmutables' USING ERRCODE = '55000'; END $$;
                CREATE TRIGGER hecho_inmutable BEFORE UPDATE OR DELETE ON "OperacionesJornada"
                FOR EACH ROW EXECUTE FUNCTION "ImpedirCambioHechoReparto"();
                CREATE TRIGGER evidencia_inmutable BEFORE UPDATE OR DELETE ON "EvidenciasReparto"
                FOR EACH ROW EXECUTE FUNCTION "ImpedirCambioHechoReparto"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntregasPush");

            migrationBuilder.DropTable(
                name: "OperacionesJornada");

            migrationBuilder.DropTable(
                name: "PruebasEntrega");

            migrationBuilder.DropTable(
                name: "AvisosReparto");

            migrationBuilder.DropTable(
                name: "InstalacionesMoviles");

            migrationBuilder.DropTable(
                name: "EvidenciasReparto");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SesionesMoviles_Id_AccesoId",
                table: "SesionesMoviles");

            migrationBuilder.DropColumn(
                name: "SalidaEnUtc",
                table: "HojasRuta");
            migrationBuilder.Sql("DROP FUNCTION \"ImpedirCambioHechoReparto\"();");
        }
    }
}
