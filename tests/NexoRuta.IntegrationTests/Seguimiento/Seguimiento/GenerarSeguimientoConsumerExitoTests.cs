
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Security;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class GenerarSeguimientoConsumerExitoTests(
    PostgresFixture postgres
) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MensajeValido_GeneraSeguimientoYConfirmaCadaEntrega(int entregas)
    {
        // ARRANGE: PostgreSQL y RabbitMQ aislados.
        var database = await postgres.CreateDatabaseAsync();

        await using var rabbitMq =
            new RabbitMqBuilder("rabbitmq:4-management")
                .Build();

        await rabbitMq.StartAsync();

        // Inicializar la API para disponer de los datos demo.
        using var api = new IntegrationApiFactory(database, "Development");
        using var client = api.CreateHttpClient();

        Guid operadorId;
        Guid accesoComercioId;
        string cadenaPostgres;

        await using (var db = database.CreateDbContext())
        {
            // Obtener la cadena original ANTES de abrir
            // la conexión mediante cualquier consulta.
            cadenaPostgres =
                db.Database.GetDbConnection().ConnectionString;

            operadorId = await db.Operadores
                .Select(x => x.Id)
                .SingleAsync();

            accesoComercioId = await db.AccesosUsuario
                .Where(x => x.ComercioId != null)
                .Select(x => x.Id)
                .SingleAsync();
        }

        var parametrosPostgres =
            new Npgsql.NpgsqlConnectionStringBuilder(cadenaPostgres);

        Assert.False(
            string.IsNullOrWhiteSpace(parametrosPostgres.Password),
            "La cadena de PostgreSQL no contiene contraseña.");
            
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/envios")
        {
            Content = JsonContent.Create(new
            {
                operadorId,
                destinatarioNombre = "Destinatario RabbitMQ",
                direccion = "San José, Uruguay",
                codigoBulto = "SEGUIMIENTO-RABBIT-001",
                pesoGramos = 1200m,
                largoCentimetros = 25m,
                anchoCentimetros = 20m,
                altoCentimetros = 15m
            })
        };

        await client.SetBearerForAccessAsync(database, accesoComercioId);

        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var json = await response.Content
            .ReadFromJsonAsync<JsonElement>();

        var envioId = json.GetProperty("id").GetGuid();

        // Obtener el mensaje real que creó el Outbox.
        Guid messageId;
        string tipo;
        byte[] contenido;

        await using (var db = database.CreateDbContext())
        {
            var mensaje = await db.OutboxMessages
                .AsNoTracking()
                .SingleAsync(x =>
                    x.EnvioId == envioId &&
                    x.Tipo == "GenerarSeguimiento");

            messageId = mensaje.Id;
            tipo = mensaje.Tipo;
            contenido = Encoding.UTF8.GetBytes(mensaje.Payload);
        }

        var uri = new Uri(rabbitMq.GetConnectionString());
        var credenciales = uri.UserInfo.Split(':', 2);

        Assert.Equal(2, credenciales.Length);

        var factory = new ConnectionFactory { Uri = uri };

        await using var conexion =
            await factory.CreateConnectionAsync();

        var opciones = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        await using var canal =
            await conexion.CreateChannelAsync(opciones);

        await RabbitMqSeguimientoTopology.DeclararAsync(canal);

        // Clave válida, estable durante toda la prueba.
        var clave = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));

        var builder = Host.CreateApplicationBuilder();
        var acknowledgements = new AckLogger(entregas);
        builder.Services.AddSingleton<ILogger<GenerarSeguimientoConsumer>>(acknowledgements);

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = uri.Host,
                ["RabbitMq:Port"] = uri.Port.ToString(),
                ["RabbitMq:Username"] =
                    Uri.UnescapeDataString(credenciales[0]),
                ["RabbitMq:Password"] =
                    Uri.UnescapeDataString(credenciales[1]),
                ["Seguimiento:EncryptionKey"] = clave
            });

        builder.Services.AddDbContext<NexoRutaDbContext>(
            options => options.UseNpgsql(cadenaPostgres));

        builder.Services.AddScoped<
            IConsultaEstadoEnvio,
            EfConsultaEstadoEnvio>();

        builder.Services.AddScoped<
            IEnlacesSeguimientoRepository,
            EfEnlacesSeguimientoRepository>();

        builder.Services
            .AddScoped<CrearEnlaceSeguimientoUseCase>();

        builder.Services
            .AddSingleton<CifradorTokenSeguimiento>();

        builder.Services
            .AddScoped<ProcesarGeneracionSeguimiento>();

        builder.Services
            .AddHostedService<GenerarSeguimientoConsumer>();

        using var host = builder.Build();

        await host.StartAsync();

        try
        {
            // ACT: publicar el mensaje original del Outbox.
            for (var i = 0; i < entregas; i++)
            {
                await canal.BasicPublishAsync(
                exchange: string.Empty,
                routingKey:
                    RabbitMqSeguimientoTopology.ColaPrincipal,
                mandatory: true,
                basicProperties: new BasicProperties
                {
                    MessageId = messageId.ToString("D"),
                    Type = tipo,
                    ContentType = "application/json",
                    Persistent = true
                },
                body: contenido);
            }

            // Esperar hasta 20 segundos el COMMIT PostgreSQL.
            var procesado = false;

            for (var intento = 0; intento < 100; intento++)
            {
                await using var db =
                    database.CreateDbContext();

                procesado = await db.ProcessedMessages
                    .AsNoTracking()
                    .AnyAsync(x =>
                        x.MessageId == messageId &&
                        x.Consumer ==
                            ProcesarGeneracionSeguimiento
                                .NombreConsumidor);

                if (procesado)
                    break;

                await Task.Delay(200);
            }

            Assert.True(
                procesado,
                "El consumidor no registró el mensaje " +
                "en PostgreSQL dentro de 20 segundos.");

            // Wait for ACK writes, not an arbitrary delay after the first COMMIT.
            await acknowledgements.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await host.StopAsync();
        }

        // ASSERT: verificar las escrituras definitivas.
        await using var verificacion =
            database.CreateDbContext();

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

        Assert.Equal(enlace.TokenHash, entrega.TokenHash);
        Assert.NotEmpty(entrega.TokenCifrado);
        var cifrador = new CifradorTokenSeguimiento(builder.Configuration);
        Assert.Equal(enlace.TokenHash,
            NexoRuta.Application.Seguimiento.Services.GeneradorTokenSeguimiento.CalcularHash(
                cifrador.Descifrar(entrega)));

        Assert.Equal(
            1,
            await verificacion.ProcessedMessages
                .CountAsync(x =>
                    x.MessageId == messageId));

        // Al cerrar el consumidor, el mensaje no debe
        // reaparecer en la cola principal.
        var principal = await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaPrincipal);

        var fallidos = await canal.QueueDeclarePassiveAsync(
            RabbitMqSeguimientoTopology.ColaFallidos);

        Assert.Equal(0u, principal.MessageCount);
        Assert.Equal(0u, fallidos.MessageCount);
        var reintento = await canal.QueueDeclarePassiveAsync(RabbitMqSeguimientoTopology.ColaReintento);
        Assert.Equal(0u, reintento.MessageCount);
        Assert.Equal(entregas, acknowledgements.Count);
    }

    private sealed class AckLogger(int expected) : ILogger<GenerarSeguimientoConsumer>
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1001 && Interlocked.Increment(ref count) == expected)
                Completed.TrySetResult();
        }
    }
}
