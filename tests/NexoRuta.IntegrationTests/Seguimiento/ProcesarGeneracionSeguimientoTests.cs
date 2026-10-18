
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Security;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class ProcesarGeneracionSeguimientoTests(
    PostgresFixture postgres
) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task PrimeraEjecucion_GuardaEnlaceEntregaYMensajeProcesado()
    {
        // ARRANGE: base PostgreSQL exclusiva de esta prueba.
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

        // Registrar un envío real desde la API.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/envios")
        {
            Content = JsonContent.Create(new
            {
                operadorId,
                destinatarioNombre = "Destinatario M05",
                direccion = "Dirección prueba transaccional",
                codigoBulto = "M05-TRANSACCION-001",
                pesoGramos = 1500m,
                largoCentimetros = 30m,
                anchoCentimetros = 20m,
                altoCentimetros = 15m
            })
        };

        await client.SetBearerForAccessAsync(database, accesoComercioId);

        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var creado = await response.Content
            .ReadFromJsonAsync<JsonElement>();

        var envioId = creado.GetProperty("id").GetGuid();

        // Simula el MessageId que proporcionará RabbitMQ.
        var messageId = await PublishedTrackingJobTestData.PublishInitialTrackingJobAsync(database, envioId);

        // Clave aleatoria exclusiva de esta prueba.
        var clave = RandomNumberGenerator.GetBytes(32);

        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Seguimiento:EncryptionKey"] =
                        Convert.ToBase64String(clave)
                })
            .Build();

        var cifrador = new CifradorTokenSeguimiento(
            configuracion);

        // ACT: ejecutar la operación transaccional.
        await using (var db = database.CreateDbContext())
        {
            var consulta = new EfConsultaEstadoEnvio(db);
            var repositorio = new EfEnlacesSeguimientoRepository(db);

            var crearEnlace = new CrearEnlaceSeguimientoUseCase(
                consulta, repositorio);

            var procesador = new ProcesarGeneracionSeguimiento(
                db, crearEnlace, cifrador);

            await procesador.EjecutarAsync(
                messageId, operadorId, envioId);
        }

        // ASSERT: verificar con un DbContext independiente.
        await using var verificacion = database.CreateDbContext();

        var enlace = Assert.Single(
            await verificacion.EnlacesSeguimiento
                .AsNoTracking()
                .Where(x => x.EnvioId == envioId)
                .ToListAsync());

        var entrega = Assert.Single(
            await verificacion.EntregasSeguimiento
                .AsNoTracking()
                .Where(x => x.EnvioId == envioId)
                .ToListAsync());

        var procesado = Assert.Single(
            await verificacion.ProcessedMessages
                .AsNoTracking()
                .Where(x => x.MessageId == messageId)
                .ToListAsync());

        // Las tres escrituras deben estar relacionadas.
        Assert.Equal(operadorId, enlace.OperadorId);
        Assert.Equal(operadorId, entrega.OperadorId);
        Assert.Equal(enlace.TokenHash, entrega.TokenHash);

        Assert.Equal(
            ProcesarGeneracionSeguimiento.NombreConsumidor,
            procesado.Consumer);

        // Recuperar el token y comprobar su hash.
        var token = cifrador.Descifrar(entrega);

        var hashRecuperado = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

        Assert.Equal(enlace.TokenHash, hashRecuperado);
        Assert.True(enlace.EstaVigente(DateTimeOffset.UtcNow));
        // ACT: RabbitMQ entrega nuevamente el mismo mensaje.
        await using (var dbReintento = database.CreateDbContext())
        {
            var consulta = new EfConsultaEstadoEnvio(dbReintento);

            var repositorio = new EfEnlacesSeguimientoRepository(
                dbReintento);

            var crearEnlace = new CrearEnlaceSeguimientoUseCase(
                consulta,
                repositorio);

            var procesador = new ProcesarGeneracionSeguimiento(
                dbReintento,
                crearEnlace,
                cifrador);

            await procesador.EjecutarAsync(
                messageId,
                operadorId,
                envioId);
        }

        // ASSERT: no se generaron registros duplicados.
        await using var verificacionReintento =
            database.CreateDbContext();

        Assert.Equal(
            1,
            await verificacionReintento.EnlacesSeguimiento
                .CountAsync(x => x.EnvioId == envioId));

        Assert.Equal(
            1,
            await verificacionReintento.EntregasSeguimiento
                .CountAsync(x => x.EnvioId == envioId));

        Assert.Equal(
            1,
            await verificacionReintento.ProcessedMessages
                .CountAsync(x =>
                    x.MessageId == messageId &&
                    x.Consumer ==
                        ProcesarGeneracionSeguimiento.NombreConsumidor));


        // ACT: llega otro mensaje distinto para el mismo envío.
        var segundoMessageId = await PublishedTrackingJobTestData.CreatePublishedTrackingJobAsync(database, operadorId, envioId);

        await using (var dbSegundoMensaje = database.CreateDbContext())
        {
            var consulta = new EfConsultaEstadoEnvio(dbSegundoMensaje);

            var repositorio = new EfEnlacesSeguimientoRepository(
                dbSegundoMensaje);

            var crearEnlace = new CrearEnlaceSeguimientoUseCase(
                consulta,
                repositorio);

            var procesador = new ProcesarGeneracionSeguimiento(
                dbSegundoMensaje,
                crearEnlace,
                cifrador);

            await procesador.EjecutarAsync(
                segundoMessageId,
                operadorId,
                envioId);
        }

        // ASSERT: siguen existiendo un enlace y una entrega.
        await using var verificacionSegundoMensaje =
            database.CreateDbContext();

        Assert.Equal(
            1,
            await verificacionSegundoMensaje.EnlacesSeguimiento
                .CountAsync(x => x.EnvioId == envioId));

        Assert.Equal(
            1,
            await verificacionSegundoMensaje.EntregasSeguimiento
                .CountAsync(x => x.EnvioId == envioId));

        // Ambos MessageId deben figurar como procesados.
        var mensajesProcesados = await verificacionSegundoMensaje
            .ProcessedMessages
            .AsNoTracking()
            .Where(x =>
                x.Consumer ==
                    ProcesarGeneracionSeguimiento.NombreConsumidor)
            .Select(x => x.MessageId)
            .ToListAsync();

        Assert.Equal(2, mensajesProcesados.Count);
        Assert.Contains(messageId, mensajesProcesados);
        Assert.Contains(segundoMessageId, mensajesProcesados);
    }

    [Fact]
    public async Task FalloAlRegistrarProcesamiento_RevierteTodaLaTransaccion()
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
        }

        // Crear el envío mediante la API existente.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/envios")
        {
            Content = JsonContent.Create(new
            {
                operadorId,
                destinatarioNombre = "Destinatario rollback M05",
                direccion = "Dirección prueba rollback",
                codigoBulto = "M05-ROLLBACK-001",
                pesoGramos = 1200m,
                largoCentimetros = 25m,
                anchoCentimetros = 20m,
                altoCentimetros = 10m
            })
        };

        await client.SetBearerForAccessAsync(database, accesoComercioId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var creado = await response.Content
            .ReadFromJsonAsync<JsonElement>();

        var envioId = creado.GetProperty("id").GetGuid();
        var messageId = await PublishedTrackingJobTestData.PublishInitialTrackingJobAsync(database, envioId);

        // Simular una falla de PostgreSQL al guardar
        // el registro de idempotencia.
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION m05_fallar_processed()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'M05_TEST_PROCESSED_ROLLBACK';
            END;
            $$;
            """);

            await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER m05_trigger_processed
            BEFORE INSERT ON "ProcessedMessages"
            FOR EACH ROW
            EXECUTE FUNCTION m05_fallar_processed();
            """);
        }

        var clave = RandomNumberGenerator.GetBytes(32);

        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Seguimiento:EncryptionKey"] =
                        Convert.ToBase64String(clave)
                })
            .Build();

        var cifrador = new CifradorTokenSeguimiento(configuracion);

        // ACT: intentar procesar el mensaje.
        await using (var db = database.CreateDbContext())
        {
            var consulta = new EfConsultaEstadoEnvio(db);
            var repositorio = new EfEnlacesSeguimientoRepository(db);

            var crearEnlace = new CrearEnlaceSeguimientoUseCase(
                consulta, repositorio);

            var procesador = new ProcesarGeneracionSeguimiento(
                db, crearEnlace, cifrador);

            var error = await Record.ExceptionAsync(
                () => procesador.EjecutarAsync(
                    messageId,
                    operadorId,
                    envioId));

            Assert.NotNull(error);

            Assert.Contains(
                "M05_TEST_PROCESSED_ROLLBACK",
                error.ToString());
        }

        // ASSERT: comprobar usando otro DbContext.
        await using var verificacion = database.CreateDbContext();

        // El envío original NO debe desaparecer.
        Assert.Equal(
            1,
            await verificacion.Envios
                .CountAsync(x => x.Id == envioId));

        // El procesamiento fallido no deja efectos parciales.
        Assert.Equal(
            0,
            await verificacion.EnlacesSeguimiento
                .CountAsync(x => x.EnvioId == envioId));

        Assert.Equal(
            0,
            await verificacion.EntregasSeguimiento
                .CountAsync(x => x.EnvioId == envioId));

        Assert.Equal(
            0,
            await verificacion.ProcessedMessages
                .CountAsync(x => x.MessageId == messageId));

        // El Outbox del alta debe conservarse.
        Assert.Equal(
            2,
            await verificacion.OutboxMessages
                .CountAsync(x => x.EnvioId == envioId));
    }

    [Fact]
    public async Task DosMensajesConcurrentes_GeneranUnaSolaEntregaInicial()
    {
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

        // Crear un envío real.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/envios")
        {
            Content = JsonContent.Create(new
            {
                operadorId,
                destinatarioNombre = "Prueba concurrencia M05",
                direccion = "San José, Uruguay",
                codigoBulto = "M05-CONCURRENCIA-001",
                pesoGramos = 1500m,
                largoCentimetros = 30m,
                anchoCentimetros = 20m,
                altoCentimetros = 15m
            })
        };

        await client.SetBearerForAccessAsync(database, accesoComercioId);

        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var creado = await response.Content
            .ReadFromJsonAsync<JsonElement>();

        var envioId = creado.GetProperty("id").GetGuid();

        // Ambos procesadores compartirán la misma clave de cifrado.
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Seguimiento:EncryptionKey"] =
                        Convert.ToBase64String(
                            RandomNumberGenerator.GetBytes(32))
                })
            .Build();

        var cifrador = new CifradorTokenSeguimiento(
            configuracion);

        var mensajeA = await PublishedTrackingJobTestData.PublishInitialTrackingJobAsync(database, envioId);
        var mensajeB = await PublishedTrackingJobTestData.CreatePublishedTrackingJobAsync(database, operadorId, envioId);

        // Permite iniciar ambos procesadores simultáneamente.
        var inicio = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ProcesarAsync(Guid messageId)
        {
            await inicio.Task;

            // Cada Worker tiene su propio DbContext.
            await using var db = database.CreateDbContext();

            var consulta = new EfConsultaEstadoEnvio(db);

            var repositorio = new EfEnlacesSeguimientoRepository(db);

            var crearEnlace = new CrearEnlaceSeguimientoUseCase(
                consulta,
                repositorio);

            var procesador = new ProcesarGeneracionSeguimiento(
                db,
                crearEnlace,
                cifrador);

            await procesador.EjecutarAsync(
                messageId,
                operadorId,
                envioId);
        }

        // ACT: iniciar dos procesamientos independientes.
        var tareaA = ProcesarAsync(mensajeA);
        var tareaB = ProcesarAsync(mensajeB);

        inicio.SetResult();

        await Task.WhenAll(tareaA, tareaB);

        // ASSERT: comprobar los registros persistidos.
        await using var verificacion = database.CreateDbContext();

        Assert.Equal(
            1,
            await verificacion.EnlacesSeguimiento
                .CountAsync(x => x.EnvioId == envioId));

        Assert.Equal(
            1,
            await verificacion.EntregasSeguimiento
                .CountAsync(x => x.EnvioId == envioId));

        var procesados = await verificacion.ProcessedMessages
            .Where(x =>
                x.Consumer ==
                    ProcesarGeneracionSeguimiento.NombreConsumidor)
            .Select(x => x.MessageId)
            .ToListAsync();

        Assert.Equal(2, procesados.Count);
        Assert.Contains(mensajeA, procesados);
        Assert.Contains(mensajeB, procesados);
    }

    [Fact]
    public async Task EnvioBloqueado_ImpideProcesamientoHastaLiberarReserva()
    {
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
            HttpMethod.Post, "api/envios")
        {
            Content = JsonContent.Create(new
            {
                operadorId,
                destinatarioNombre = "Prueba bloqueo M05",
                direccion = "San José, Uruguay",
                codigoBulto = "M05-LOCK-001",
                pesoGramos = 1000m,
                largoCentimetros = 20m,
                anchoCentimetros = 15m,
                altoCentimetros = 10m
            })
        };

        await client.SetBearerForAccessAsync(database, accesoComercioId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var creado = await response.Content
            .ReadFromJsonAsync<JsonElement>();

        var envioId = creado.GetProperty("id").GetGuid();
        var messageId = await PublishedTrackingJobTestData.PublishInitialTrackingJobAsync(database, envioId);

        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Seguimiento:EncryptionKey"] =
                        Convert.ToBase64String(
                            RandomNumberGenerator.GetBytes(32))
                })
            .Build();

        var cifrador = new CifradorTokenSeguimiento(configuracion);

        // Primera conexión: mantener bloqueado el envío.
        await using var dbBloqueo = database.CreateDbContext();

        await using var transaccion =
            await dbBloqueo.Database.BeginTransactionAsync();

        var bloqueados = await dbBloqueo.Database.SqlQuery<Guid>(
            $"""
        SELECT "Id" AS "Value"
        FROM "Envios"
        WHERE "Id" = {envioId}
          AND "OperadorId" = {operadorId}
        FOR UPDATE
        """)
            .ToListAsync();

        Assert.Single(bloqueados);

        try
        {
            // Segunda conexión: intentar procesar el mismo envío.
            await using var dbIntento = database.CreateDbContext();

            // Mantener abierta la conexión para conservar lock_timeout.
            await dbIntento.Database.OpenConnectionAsync();

            await dbIntento.Database.ExecuteSqlRawAsync(
                "SET lock_timeout = '1s'");

            var consulta = new EfConsultaEstadoEnvio(dbIntento);
            var repositorio = new EfEnlacesSeguimientoRepository(
                dbIntento);

            var crearEnlace = new CrearEnlaceSeguimientoUseCase(
                consulta, repositorio);

            var procesador = new ProcesarGeneracionSeguimiento(
                dbIntento, crearEnlace, cifrador);

            var error = await Record.ExceptionAsync(
                () => procesador.EjecutarAsync(
                    messageId, operadorId, envioId));

            Assert.NotNull(error);

            var postgresError = Assert.IsType<Npgsql.PostgresException>(
                error.GetBaseException());

            Assert.Equal("55P03", postgresError.SqlState);
        }
        finally
        {
            // Liberar el bloqueo incluso si la aserción falla.
            await transaccion.RollbackAsync();
        }

        // El intento bloqueado no debe haber guardado nada.
        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(0, await db.EnlacesSeguimiento.CountAsync());
            Assert.Equal(0, await db.EntregasSeguimiento.CountAsync());
            Assert.Equal(0, await db.ProcessedMessages.CountAsync());
        }

        // Ahora, sin bloqueo, el mismo mensaje debe procesarse.
        await using (var db = database.CreateDbContext())
        {
            var crearEnlace = new CrearEnlaceSeguimientoUseCase(
                new EfConsultaEstadoEnvio(db),
                new EfEnlacesSeguimientoRepository(db));

            var procesador = new ProcesarGeneracionSeguimiento(
                db, crearEnlace, cifrador);

            await procesador.EjecutarAsync(
                messageId, operadorId, envioId);
        }

        await using var verificacion = database.CreateDbContext();

        Assert.Equal(1, await verificacion.EnlacesSeguimiento.CountAsync());
        Assert.Equal(1, await verificacion.EntregasSeguimiento.CountAsync());
        Assert.Equal(1, await verificacion.ProcessedMessages.CountAsync());
    }


}
