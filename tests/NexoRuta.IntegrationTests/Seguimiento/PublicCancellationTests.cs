using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Envios;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Worker.Messaging;
using Npgsql;

namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class PublicCancellationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task CodigoSinVencimientoCancelaUnaVezYAuditaSinInventarUsuario()
    {
        using var p = await Preparar();
        var comando = await Solicitar(p);
        await using var db = p.Data.CreateDbContext();
        await db.CodigosCancelacionPublica.ExecuteUpdateAsync(s => s.SetProperty(c => c.CreadoEnUtc, DateTimeOffset.UtcNow.AddMonths(-4)));
        using var respuesta = await Confirmar(p, comando.Codigo);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        using var repeticion = await Confirmar(p, comando.Codigo);
        Assert.Equal(HttpStatusCode.OK, repeticion.StatusCode);
        Assert.Equal(EstadoEnvio.Cancelado, (await db.Envios.SingleAsync()).Estado);
        var auditoria = await db.CancelacionesEnvio.SingleAsync();
        Assert.Null(auditoria.UsuarioId); Assert.Null(auditoria.AccesoId); Assert.Null(auditoria.TipoActor); Assert.Null(auditoria.OrganizacionActorId);
        Assert.Equal(comando.CodigoId, auditoria.AutorizacionPublicaId);
        var history = Assert.Single(await db.EnvioEventos.Where(x => x.CancelacionEnvioId == auditoria.Id).ToArrayAsync());
        Assert.Equal(("Publico", comando.CodigoId, "Cancelado"),
            (history.ActorTipo, history.AutorizacionPublicaId, history.EstadoNuevo));
        Assert.Null(history.ActorUsuarioId); Assert.Null(history.ActorAccesoId);
        Assert.NotNull((await db.CodigosCancelacionPublica.SingleAsync()).ConsumidoEnUtc);
        Assert.Empty(await db.AvisosReparto.ToArrayAsync());
        Assert.DoesNotContain("destinatario", await respuesta.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.GetService<IMigrator>().MigrateAsync("20261019225313_RequireRecipientEmail"));
        Assert.Equal("55000", Assert.IsType<PostgresException>(error.InnerException).SqlState); // No elimina hechos públicos para volver al modelo anterior.
    }

    [Fact]
    public async Task CancelacionPublicaAdmiteEnDevolucion()
    {
        using var p = await Preparar();
        await using (var db = p.Data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == p.Envio.Id);
            envio.CambiarEstado(EstadoEnvio.EnDeposito, envio.Version);
            await db.SaveChangesAsync();
            envio.CambiarEstado(EstadoEnvio.AsignadoRuta, envio.Version);
            await db.SaveChangesAsync();
            envio.CambiarEstado(EstadoEnvio.EnTransito, envio.Version);
            await db.SaveChangesAsync();
            envio.CambiarEstado(EstadoEnvio.NoEntregado, envio.Version);
            await db.SaveChangesAsync();
            envio.CambiarEstado(EstadoEnvio.EnDevolucion, envio.Version);
            await db.SaveChangesAsync();
        }

        var codigo = await Solicitar(p);
        using var confirmacion = await Confirmar(p, codigo.Codigo);
        Assert.Equal(HttpStatusCode.OK, confirmacion.StatusCode);
        using var repetir = await Confirmar(p, codigo.Codigo);
        Assert.Equal(HttpStatusCode.OK, repetir.StatusCode);
        await using var final = p.Data.CreateDbContext();
        Assert.Equal(EstadoEnvio.Cancelado, (await final.Envios.SingleAsync(e => e.Id == p.Envio.Id)).Estado);
        Assert.Single(await final.CancelacionesEnvio.ToArrayAsync());
    }

    [Fact]
    public async Task CincoErroresPersistenAunqueHttpSea400YReemplazoInvalidaCodigoAnterior()
    {
        using var p = await Preparar(); var viejo = await Solicitar(p);
        for (var i = 0; i < 5; i++)
        {
            using var error = await Confirmar(p, "ZZZZZZZZZZZZ"); Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        }
        using var agotado = await Confirmar(p, viejo.Codigo); Assert.Equal(HttpStatusCode.BadRequest, agotado.StatusCode);
        await using var db = p.Data.CreateDbContext();
        Assert.Equal(5, (await db.CodigosCancelacionPublica.AsNoTracking().SingleAsync()).Intentos);
        Assert.Equal(EstadoEnvio.Admitido, (await db.Envios.SingleAsync()).Estado);
        await db.CodigosCancelacionPublica.ExecuteUpdateAsync(s => s.SetProperty(c => c.CreadoEnUtc, DateTimeOffset.UtcNow.AddMinutes(-2)));
        var nuevo = await Solicitar(p);
        using var anterior = await Confirmar(p, viejo.Codigo); Assert.Equal(HttpStatusCode.BadRequest, anterior.StatusCode);
        using var actual = await Confirmar(p, nuevo.Codigo); Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        Assert.Equal(2, await db.CodigosCancelacionPublica.CountAsync());
    }

    [Fact]
    public async Task EnlaceDeLecturaNoAutorizaYNoSePuedeUsarCodigoDeOtroOperador()
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        string otroToken;
        await using (var db = p.Data.CreateDbContext())
        {
            var otro = await SeguimientoTestData.CreateEnvioAsync(db);
            otroToken = await SeguimientoTestData.CreateActiveLinkAsync(db, otro);
        }
        using var respuesta = await p.Client.PostAsJsonAsync($"/api/public/seguimiento/{otroToken}/cancelacion", new { codigo = comando.Codigo });
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        using var sinCodigo = await p.Client.PostAsJsonAsync($"{p.Url}/cancelacion", new { });
        Assert.Equal(HttpStatusCode.BadRequest, sinCodigo.StatusCode);
        await using var comprobar = p.Data.CreateDbContext(); Assert.Empty(await comprobar.CancelacionesEnvio.ToArrayAsync());
    }

    [Fact]
    public async Task PeticionRepetidaSeLimitaYNoDuplicaCorreoNiExponeContacto()
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        using var respuesta = await p.Client.PostAsync(p.Url + "/cancelacion/codigo", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, respuesta.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), respuesta.Headers.RetryAfter!.Delta);
        await using var db = p.Data.CreateDbContext(); Assert.Single(await db.OutboxMessages.ToArrayAsync());
        var registro = await db.CodigosCancelacionPublica.SingleAsync();
        Assert.NotEqual(comando.Codigo, registro.Hash);
        Assert.DoesNotContain(comando.Codigo, (await db.OutboxMessages.SingleAsync()).Payload);
        Assert.DoesNotContain(comando.Destinatario, (await db.OutboxMessages.SingleAsync()).Payload);
        using var malformado = await p.Client.PostAsJsonAsync(p.Url + "/cancelacion", new { codigo = comando.Codigo, envioId = p.Envio.Id });
        Assert.Equal(HttpStatusCode.BadRequest, malformado.StatusCode);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DespuesDeEntregaFisicaNoCancelaAunqueLaPruebaEstePendiente(bool pruebaCompleta)
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        await Entregar(p, pruebaCompleta);
        using var respuesta = await Confirmar(p, comando.Codigo); Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        using var solicitar = await p.Client.PostAsync(p.Url + "/cancelacion/codigo", null); Assert.Equal(HttpStatusCode.NotFound, solicitar.StatusCode);
        await using var db = p.Data.CreateDbContext(); Assert.Empty(await db.CancelacionesEnvio.ToArrayAsync());
    }

    [Fact]
    public async Task ConfirmacionesConcurrentesConservanUnaSolaCancelacion()
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        var respuestas = await Task.WhenAll(Confirmar(p, comando.Codigo), Confirmar(p, comando.Codigo));
        foreach (var respuesta in respuestas) { Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode); respuesta.Dispose(); }
        await using var db = p.Data.CreateDbContext(); Assert.Single(await db.CancelacionesEnvio.ToArrayAsync());
        Assert.Equal(1, (await db.Envios.SingleAsync()).Version);
    }

    [Fact]
    public async Task WorkerRestringidoEnviaCorreoUnaVezYReintentaFalloSmtpSinCambiarCodigo()
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        await Publicar(p, comando.MessageId);
        await using var db = await WorkerDb(p);
        var correo = new CorreoPrueba { Fallar = true };
        var processor = new ProcesarCorreoCodigoCancelacion(db, p.Proteccion, correo);
        await Assert.ThrowsAsync<IOException>(() => processor.EjecutarAsync(comando.MessageId, default));
        await using (var owner = p.Data.CreateDbContext()) Assert.Null((await owner.CodigosCancelacionPublica.SingleAsync()).CorreoEnviadoEnUtc);
        correo.Fallar = false;
        await processor.EjecutarAsync(comando.MessageId, default);
        await processor.EjecutarAsync(comando.MessageId, default);
        Assert.Single(correo.Enviados); Assert.Equal(comando.Destinatario, correo.Enviados[0].Destino);
        Assert.Contains(comando.Codigo, correo.Enviados[0].Cuerpo.Replace("-", "", StringComparison.Ordinal));
        var denied = await Assert.ThrowsAsync<PostgresException>(() => db.CodigosCancelacionPublica.ToArrayAsync()); Assert.Equal("42501", denied.SqlState);
    }

    [Fact]
    public async Task RecipientCancellationEmailUsesOnlyExplicitPublicBrandAndAccessibleLogo()
    {
        using var p = await Preparar();
        var logo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/S3sAAAAASUVORK5CYII=");
        await using (var owner = p.Data.CreateDbContext())
        {
            owner.VersionesIdentidadVisualOperador.Add(new NexoRuta.Domain.Administracion.VersionIdentidadVisualOperador(
                p.Envio.OperadorId, 1, "Marca Destinataria", "contacto@marca.test", "+598 99 123 456",
                "#FFFF00", null, logo, "image/png", p.Envio.CreadoPorUsuarioId, DateTimeOffset.UtcNow));
            await owner.SaveChangesAsync();
        }
        var command = await Solicitar(p);
        Assert.Equal("Marca Destinataria", command.Identidad!.Nombre);
        Assert.Equal("contacto@marca.test", command.Identidad.CorreoPublico);

        await Publicar(p, command.MessageId);
        await using var db = await WorkerDb(p);
        var mail = new CorreoPrueba();
        await new ProcesarCorreoCodigoCancelacion(db, p.Proteccion, mail).EjecutarAsync(command.MessageId, default);

        Assert.Single(mail.Enviados);
        Assert.Contains("Marca Destinataria", mail.Enviados[0].Cuerpo);
        Assert.Contains("contacto@marca.test", mail.Enviados[0].Cuerpo);
        Assert.Contains("Marca Destinataria", mail.HtmlBody);
        Assert.Contains("mailto:contacto@marca.test", mail.HtmlBody);
        Assert.Contains("cid:nexoruta-operator-logo", mail.HtmlBody);
        Assert.Equal(logo, mail.InlineLogo);
    }

    [Theory]
    [InlineData("reemplazado")] [InlineData("consumido")] [InlineData("entregado")]
    public async Task WorkerOmiteCorreoQueYaNoSirve(string causa)
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        if (causa == "consumido") { using var respuesta = await Confirmar(p, comando.Codigo); Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode); }
        if (causa == "entregado") await Entregar(p, true);
        if (causa == "reemplazado")
        {
            await using var db = p.Data.CreateDbContext();
            await db.CodigosCancelacionPublica.ExecuteUpdateAsync(s => s.SetProperty(c => c.CreadoEnUtc, DateTimeOffset.UtcNow.AddMinutes(-2)));
            await Solicitar(p);
        }
        await Publicar(p, comando.MessageId);
        await using var workerDb = await WorkerDb(p); var correo = new CorreoPrueba();
        await new ProcesarCorreoCodigoCancelacion(workerDb, p.Proteccion, correo).EjecutarAsync(comando.MessageId, default);
        Assert.Empty(correo.Enviados);
    }

    [Fact]
    public async Task ApiNoPuedeInvocarNucleoPrivadoNiLeerCodigosDirectamente()
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        await using var db = p.Restricted.CreateDbContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.CodigosCancelacionPublica.ToArrayAsync()); Assert.Equal("42501", error.SqlState);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.apply_shipment_cancellation({p.Envio.Id},{0L},{"intento"},{Guid.NewGuid()},{Guid.NewGuid()},{Guid.NewGuid()},{comando.CodigoId})"));
        Assert.Equal("42501", denied.SqlState);
        using var desconocido = await p.Client.PostAsync("/api/public/seguimiento/" + new string('f', 64) + "/cancelacion/codigo", null);
        Assert.Equal(HttpStatusCode.NotFound, desconocido.StatusCode);
    }

    [Fact]
    public async Task MigracionPuedeRevertirseSinHechosPublicosYVolverAAplicarse()
    {
        var data = await postgres.CreateDatabaseAsync(); await using var db = data.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261019225313_RequireRecipientEmail");
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task OutboxPublicadorRabbitYConsumidorEntreganElSobreAutorizado()
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build(); await rabbit.StartAsync();
        var uri = new Uri(rabbit.GetConnectionString()); var credenciales = uri.UserInfo.Split(':');
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["RabbitMq:Host"] = uri.Host, ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:Username"] = Uri.UnescapeDataString(credenciales[0]), ["RabbitMq:Password"] = Uri.UnescapeDataString(credenciales[1])
        }).Build();
        var cs = await WorkerConnection(p);
        await using var db = new NexoRutaDbContext(new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(cs).Options);
        var claim = await new EfOutboxRepository(db).ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.RabbitMq);
        Assert.NotNull(claim); Assert.Equal(comando.MessageId, claim.Id);
        Assert.NotNull(await db.Database.SqlQuery<string?>($"SELECT nexoruta_app.prepare_public_cancel_email({comando.MessageId}) AS \"Value\"").SingleAsync());
        await using var publisher = new RabbitMqOutboxPublisher(config);
        await publisher.PublishCancellationCodeAsync(claim, default);
        await using var connection = await new ConnectionFactory { Uri = uri }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var mensaje = await channel.BasicGetAsync(CancellationCodeEmailConsumer.Queue, false);
        Assert.NotNull(mensaje); Assert.Equal(comando.MessageId.ToString("D"), mensaje.BasicProperties.MessageId);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(mensaje.Body.Span));
        await channel.BasicNackAsync(mensaje.DeliveryTag, false, true);
        var correo = new CorreoPrueba();
        await using var servicios = new ServiceCollection().AddDbContext<NexoRutaDbContext>(o => o.UseNexoRutaPostgres(cs))
            .AddSingleton(p.Proteccion).AddSingleton<ICorreoSender>(correo).AddScoped<ProcesarCorreoCodigoCancelacion>().BuildServiceProvider();
        using var consumer = new CancellationCodeEmailConsumer(config, servicios.GetRequiredService<IServiceScopeFactory>(), p.Factory.Services.GetRequiredService<ILogger<CancellationCodeEmailConsumer>>());
        await using (var scope = servicios.CreateAsyncScope())
        {
            var comprobar = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
            Assert.NotNull(await comprobar.Database.SqlQuery<string?>($"SELECT nexoruta_app.prepare_public_cancel_email({comando.MessageId}) AS \"Value\"").SingleAsync());
        }
        await consumer.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var owner = p.Data.CreateDbContext();
            while (!await owner.CodigosCancelacionPublica.AnyAsync(c => c.MensajeId == comando.MessageId && c.CorreoEnviadoEnUtc != null, timeout.Token))
            {
                if (consumer.ExecuteTask is { IsCompleted: true } tarea) await tarea;
                await Task.Delay(50, timeout.Token);
            }
            Assert.Single(correo.Enviados);
        }
        finally { await consumer.StopAsync(default); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CodigoNoEvitaVencimientoORevocacionDelEnlace(bool revocado)
    {
        using var p = await Preparar(); var comando = await Solicitar(p);
        await using var db = p.Data.CreateDbContext();
        if (revocado)
            await db.EnlacesSeguimiento.ExecuteUpdateAsync(s => s.SetProperty(e => e.RevocadoEnUtc, DateTimeOffset.UtcNow));
        else
            await db.EnlacesSeguimiento.ExecuteUpdateAsync(s => s.SetProperty(e => e.CreadoEnUtc, DateTimeOffset.UtcNow.AddDays(-2)).SetProperty(e => e.ExpiraEnUtc, DateTimeOffset.UtcNow.AddDays(-1)));
        using var respuesta = await Confirmar(p, comando.Codigo); Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Empty(await db.CancelacionesEnvio.ToArrayAsync());
    }

    private async Task<Preparado> Preparar()
    {
        var data = await postgres.CreateDatabaseAsync();
        await using var db = data.CreateDbContext(); var envio = await SeguimientoTestData.CreateEnvioAsync(db);
        var token = await SeguimientoTestData.CreateActiveLinkAsync(db, envio);
        var restricted = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var factory = new IntegrationApiFactory(restricted, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        var client = factory.CreateHttpClient();
        return new(data, restricted, factory, client, envio, token, factory.Services.GetRequiredService<IDataProtectionProvider>());
    }
    private static async Task<CorreoCodigoCancelacion> Solicitar(Preparado p)
    {
        using var respuesta = await p.Client.PostAsync(p.Url + "/cancelacion/codigo", null);
        Assert.Equal(HttpStatusCode.Accepted, respuesta.StatusCode);
        Assert.Equal("no-store", respuesta.Headers.CacheControl!.ToString());
        Assert.Equal("", await respuesta.Content.ReadAsStringAsync());
        await using var db = p.Data.CreateDbContext();
        var payload = await db.OutboxMessages.Where(x => x.Tipo == OutboxMessageTypes.CancellationCodeEmail).OrderByDescending(x => x.CreadoEnUtc).Select(x => x.Payload).FirstAsync();
        var protegido = JsonDocument.Parse(payload).RootElement.GetProperty("protectedCommand").GetString()!;
        return JsonSerializer.Deserialize<CorreoCodigoCancelacion>(p.Proteccion.CreateProtector(CorreoCodigoCancelacion.ProtectionPurpose).Unprotect(protegido))!;
    }
    private static Task<HttpResponseMessage> Confirmar(Preparado p, string codigo) => p.Client.PostAsJsonAsync(p.Url + "/cancelacion", new { codigo });
    private static async Task Entregar(Preparado p, bool completa)
    {
        await using var db = p.Data.CreateDbContext(); var envio = await db.Envios.SingleAsync(x => x.Id == p.Envio.Id);
        envio.CambiarEstado(EstadoEnvio.EnDeposito, envio.Version);
        await db.SaveChangesAsync();
        envio.CambiarEstado(EstadoEnvio.AsignadoRuta, envio.Version);
        await db.SaveChangesAsync();
        var instante = DateTimeOffset.UtcNow;
        envio.CambiarEstado(EstadoEnvio.EnTransito, envio.Version);
        await db.SaveChangesAsync();
        envio.DeclararEntregaFisica(instante, instante, 60, completa, envio.Version);
        await db.SaveChangesAsync();
    }
    private static async Task Publicar(Preparado p, Guid mensaje)
    {
        await using var db = p.Data.CreateDbContext();
        await db.OutboxMessages.Where(x => x.Id == mensaje).ExecuteUpdateAsync(s => s.SetProperty(x => x.Estado, OutboxEstado.Publicado));
    }
    private static async Task<NexoRutaDbContext> WorkerDb(Preparado p)
        => new(new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(await WorkerConnection(p)).Options);

    private static async Task<string> WorkerConnection(Preparado p)
    {
        var rol = "mail_test_" + Guid.NewGuid().ToString("N"); var clave = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var conn = new NpgsqlConnection(p.Data.ConnectionString); await conn.OpenAsync();
        await using var crear = new NpgsqlCommand($"CREATE ROLE {rol} LOGIN PASSWORD '{clave}' NOSUPERUSER NOBYPASSRLS; GRANT nexoruta_worker TO {rol}", conn); await crear.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(p.Data.ConnectionString) { Username = rol, Password = clave }.ConnectionString;
    }
    private sealed record Preparado(IntegrationDatabase Data, IntegrationDatabase Restricted, IntegrationApiFactory Factory,
        HttpClient Client, Envio Envio, string Token, IDataProtectionProvider Proteccion) : IDisposable
    {
        public string Url => $"/api/public/seguimiento/{Token}";
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }
    }
    private sealed class CorreoPrueba : ICorreoSender
    {
        public bool Fallar { get; set; }
        public List<(string Destino, string Cuerpo)> Enviados { get; } = [];
        public string? HtmlBody { get; private set; }
        public byte[]? InlineLogo { get; private set; }
        public Task EnviarAsync(string destinatario, string asunto, string cuerpo, CancellationToken cancellationToken = default)
        { if (Fallar) throw new IOException("SMTP no disponible"); Enviados.Add((destinatario, cuerpo)); return Task.CompletedTask; }
        public Task EnviarHtmlAsync(string destinatario, string asunto, string cuerpoTexto, string cuerpoHtml,
            byte[]? imagenInline, string? tipoImagen, CancellationToken ct)
        {
            if (Fallar) throw new IOException("SMTP no disponible");
            Enviados.Add((destinatario, cuerpoTexto));
            HtmlBody = cuerpoHtml;
            InlineLogo = imagenInline;
            return Task.CompletedTask;
        }
    }
}
