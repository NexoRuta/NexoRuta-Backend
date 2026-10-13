
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class CreacionAutomaticaSeguimientoTests(
    PostgresFixture postgres
) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task CrearEnvio_GuardaDosMensajesOutboxPendientes()
    {
        // ARRANGE: PostgreSQL aislado mediante Testcontainers.
        var database = await postgres.CreateDatabaseAsync();

        using var factory = new IntegrationApiFactory(database, "Development");
        using var client = factory.CreateHttpClient();

        Guid operadorId;
        Guid accesoComercioId;

        await using (var db = database.CreateDbContext())
        {
            operadorId = await db.Operadores
                .Select(x => x.Id)
                .SingleAsync();

            accesoComercioId = await db.AccesosUsuario
                .Where(x => x.ComercioId != null)
                .Select(x => x.Id)
                .SingleAsync();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "api/envios")
        {
            Content = JsonContent.Create(new
            {
                operadorId,
                destinatarioNombre = "Destinatario Outbox",
                direccion = "Dirección de prueba",
                codigoBulto = "B-OUTBOX-001",
                pesoGramos = 1250m,
                largoCentimetros = 30m,
                anchoCentimetros = 20m,
                altoCentimetros = 10m
            })
        };

        request.Headers.Add(
            "X-NexoRuta-Acceso",
            accesoComercioId.ToString());

        // ACT: Registrar el envío.
        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var creado = await response.Content
            .ReadFromJsonAsync<JsonElement>();

        var envioId = creado.GetProperty("id").GetGuid();

        // ASSERT: Verificar los registros persistidos.
        await using var verificacion = database.CreateDbContext();

        var envio = await verificacion.Envios
            .AsNoTracking()
            .SingleAsync(x => x.Id == envioId);

        Assert.Equal(operadorId, envio.OperadorId);

        var mensajes = await verificacion.OutboxMessages
            .AsNoTracking()
            .Where(x => x.EnvioId == envioId)
            .ToListAsync();

        // Deben existir exactamente dos mensajes.
        Assert.Equal(2, mensajes.Count);

        var trabajo = mensajes.Single(
            x => x.Tipo == "GenerarSeguimiento");

        var evento = mensajes.Single(
            x => x.Tipo == "EnvioCreado");

        // RabbitMQ: tarea de seguimiento.
        Assert.Equal(OutboxDestino.RabbitMq, trabajo.Destino);
        Assert.Equal(OutboxEstado.Pendiente, trabajo.Estado);

        // Kafka: evento de negocio.
        Assert.Equal(OutboxDestino.Kafka, evento.Destino);
        Assert.Equal(OutboxEstado.Pendiente, evento.Estado);

        foreach (var mensaje in mensajes)
        {
            Assert.Equal(operadorId, mensaje.OperadorId);
            Assert.Equal(0, mensaje.Intentos);
            Assert.Null(mensaje.PublicadoEnUtc);

            // Ambos payloads deben ser JSON válido.
            using var documento = JsonDocument.Parse(mensaje.Payload);

            Assert.Equal(
                envioId,
                documento.RootElement.GetProperty("envioId").GetGuid());

            Assert.Equal(
                operadorId,
                documento.RootElement.GetProperty("operadorId").GetGuid());
        }

        // El Worker todavía no procesó los mensajes.
        Assert.Equal(
            0,
            await verificacion.EnlacesSeguimiento
                .CountAsync(x => x.EnvioId == envioId));
    }


    [Fact]
    public async Task CrearEnvio_SiFallaOutbox_NoGuardaAltaParcial()
    {
        // ARRANGE: PostgreSQL exclusivo de esta prueba.
        var database = await postgres.CreateDatabaseAsync();

        using var factory = new IntegrationApiFactory(database, "Development");
        using var client = factory.CreateHttpClient();

        Guid operadorId;
        Guid accesoComercioId;

        await using (var db = database.CreateDbContext())
        {
            operadorId = await db.Operadores
                .Select(x => x.Id)
                .SingleAsync();

            accesoComercioId = await db.AccesosUsuario
                .Where(x => x.ComercioId != null)
                .Select(x => x.Id)
                .SingleAsync();

            Assert.Equal(0, await db.Envios.CountAsync());

            // Simular fallo al publicar el registro Kafka
            // en la tabla OutboxMessages.
            await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION m05_rechazar_outbox_kafka()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW."Destino" = 'Kafka' THEN
                    RAISE EXCEPTION 'M05_TEST_OUTBOX_FALLO';
                END IF;

                RETURN NEW;
            END;
            $$;
            """);

            await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER m05_fallo_outbox_kafka
            BEFORE INSERT ON "OutboxMessages"
            FOR EACH ROW
            EXECUTE FUNCTION m05_rechazar_outbox_kafka();
            """);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/envios")
        {
            Content = JsonContent.Create(new
            {
                operadorId,
                destinatarioNombre = "Destinatario rollback",
                direccion = "Dirección de prueba",
                codigoBulto = "B-ROLLBACK-001",
                pesoGramos = 1250m,
                largoCentimetros = 30m,
                anchoCentimetros = 20m,
                altoCentimetros = 10m
            })
        };

        request.Headers.Add(
            "X-NexoRuta-Acceso",
            accesoComercioId.ToString());

        // ACT: el alta debe fallar debido al trigger.
        HttpStatusCode? codigo = null;

        var excepcion = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.SendAsync(request);
            codigo = response.StatusCode;
        });

        if (excepcion is not null)
        {
            Assert.Contains(
                "M05_TEST_OUTBOX_FALLO",
                excepcion.ToString());
        }
        else
        {
            Assert.Equal(
                HttpStatusCode.InternalServerError,
                codigo);
        }

        // ASSERT: ninguna inserción parcial debe sobrevivir.
        await using var verificacion = database.CreateDbContext();

        Assert.Equal(0, await verificacion.Envios.CountAsync());
        Assert.Equal(0, await verificacion.Bultos.CountAsync());
        Assert.Equal(0, await verificacion.Destinatarios.CountAsync());
        Assert.Equal(0, await verificacion.Direcciones.CountAsync());
        Assert.Equal(0, await verificacion.OutboxMessages.CountAsync());
    }

}
