using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Notificaciones;
using NexoRuta.Infrastructure.Notificaciones;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker;
using NexoRuta.Worker.Messaging;
using Npgsql;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Notificaciones;

public sealed partial class PushOutboxTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RegistroCifradoYEnvioRestringidoNoConfundenAceptacionConLectura()
    {
        using var p = await PrepararPush(2);
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.False(await worker.ProcesarSiguienteAsync(default));
        var enviado = Assert.Single(transporte.Enviados);
        Assert.Equal(p.Fids.Order(), enviado.Fids.Order());
        Assert.Equal(new[] { "accesoId", "notificacionId", "operadorId", "v" }, enviado.Data.Keys.Order());
        Assert.Equal(p.Aviso.Id.ToString(), enviado.Data["notificacionId"]);
        Assert.DoesNotContain("destinatario", JsonSerializer.Serialize(enviado.Data), StringComparison.OrdinalIgnoreCase);
        await using var db = p.Data.CreateDbContext();
        var filas = await db.InstalacionesMoviles.ToArrayAsync();
        Assert.All(filas, i =>
        {
            Assert.Equal(12, i.Nonce.Length); Assert.Equal(16, i.Tag.Length);
            Assert.DoesNotContain(p.Fids, fid => System.Text.Encoding.UTF8.GetBytes(fid).SequenceEqual(i.FidCifrado));
        });
        Assert.Equal(2, await db.EntregasPush.CountAsync());
        Assert.Equal(OutboxEstado.Publicado, (await db.OutboxMessages.SingleAsync(x => x.Id == p.Mensaje)).Estado);
        var aviso = await db.AvisosReparto.SingleAsync();
        Assert.Null(aviso.RecibidoEnUtc); Assert.Null(aviso.AbiertoEnUtc); Assert.Null(aviso.ConfirmadoEnUtc);
        Assert.Equal("AsignadoRuta", (await db.Envios.SingleAsync(x => x.Id == aviso.EnvioId)).Estado.ToString());
        using var recibido = await p.Client.PostAsJsonAsync($"/api/mobile/notificaciones/{aviso.Id}/confirmaciones", new { Tipo = "Recibido" });
        Assert.Equal(HttpStatusCode.OK, recibido.StatusCode);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.AvisosReparto.SingleAsync()).RecibidoEnUtc);
        Assert.Null((await db.AvisosReparto.SingleAsync()).AbiertoEnUtc);
    }

    [Fact]
    public async Task FalloParcialReintentaSoloElDestinoQueNoFueAceptado()
    {
        using var p = await PrepararPush(2);
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte { Responder = (m, intento) => intento == 1
            ? [new(EstadoEnvioPush.Aceptado, "aceptado-primero"), new(EstadoEnvioPush.Reintentar)]
            : m.Fids.Select(_ => new ResultadoEnvioPush(EstadoEnvioPush.Aceptado, "aceptado-reintento")).ToArray() };
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        var antes = DateTimeOffset.UtcNow;
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        await using (var db = p.Data.CreateDbContext())
        {
            var mensaje = await db.OutboxMessages.SingleAsync(x => x.Id == p.Mensaje);
            Assert.Equal(OutboxEstado.Error, mensaje.Estado);
            Assert.InRange(mensaje.ProximoIntentoEnUtc, antes.AddSeconds(10), DateTimeOffset.UtcNow.AddSeconds(25));
            Assert.Single(await db.EntregasPush.ToArrayAsync());
            await db.OutboxMessages.Where(x => x.Id == p.Mensaje).ExecuteUpdateAsync(s => s.SetProperty(x => x.ProximoIntentoEnUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Equal(2, transporte.Enviados.Count);
        Assert.Equal(transporte.Enviados[0].Fids.Skip(1), Assert.Single(transporte.Enviados.Skip(1)).Fids);
        await using var final = p.Data.CreateDbContext();
        Assert.Equal(2, await final.EntregasPush.CountAsync());
        Assert.Equal(OutboxEstado.Publicado, (await final.OutboxMessages.SingleAsync(x => x.Id == p.Mensaje)).Estado);
    }

    [Theory]
    [InlineData("revocada")] [InlineData("expirada")] [InlineData("credencial")]
    [InlineData("perfil")] [InlineData("permiso")] [InlineData("desactivada")] [InlineData("no-permitida")]
    public async Task SesionORegistroNoVigenteNuncaRecibePush(string motivo)
    {
        using var p = await PrepararPush(1);
        await using (var db = p.Data.CreateDbContext())
        {
            var i = await db.InstalacionesMoviles.SingleAsync();
            if (motivo == "revocada") await db.SesionesMoviles.ExecuteUpdateAsync(s => s.SetProperty(x => x.RevocadaEnUtc, DateTimeOffset.UtcNow));
            if (motivo == "expirada") await db.SesionesMoviles.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.CreadaEnUtc, DateTimeOffset.UtcNow.AddDays(-31)).SetProperty(x => x.ExpiraEnUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
            if (motivo == "credencial") { var u = await db.Usuarios.SingleAsync(x => x.Id == db.SesionesMoviles.Select(s => s.UsuarioId).First()); u.RevocarSesionesPorCambioDePassword(); await db.SaveChangesAsync(); }
            if (motivo == "perfil") await db.Repartidores.ExecuteUpdateAsync(s => s.SetProperty(x => x.Activo, false));
            if (motivo == "permiso") await db.RolesPermisos.Where(rp => db.Permisos.Any(pm => pm.Id == rp.PermisoId && pm.Codigo == "reparto.ejecutar")).ExecuteDeleteAsync();
            if (motivo == "desactivada") await db.InstalacionesMoviles.ExecuteUpdateAsync(s => s.SetProperty(x => x.Activa, false));
            if (motivo == "no-permitida") await db.InstalacionesMoviles.ExecuteUpdateAsync(s => s.SetProperty(x => x.Permitida, false));
        }
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Empty(transporte.Enviados);
        await using var comprobar = p.Data.CreateDbContext();
        Assert.Null((await comprobar.AvisosReparto.SingleAsync()).ConfirmadoEnUtc);
    }

    [Fact]
    public async Task RespuestaDeFidAnteriorNoDesactivaElRegistroQueRotoDuranteElEnvio()
    {
        using var p = await PrepararPush(1);
        await using var services = await ServiciosWorker(p);
        var nuevo = Guid.NewGuid().ToString("N");
        var transporte = new Transporte { AntesDeResponder = async intento =>
        {
            if (intento != 1) return;
            await using var db = p.Data.CreateDbContext();
            var instalacion = await db.InstalacionesMoviles.SingleAsync();
            new CifradorRegistroPush(p.Config).Cifrar(instalacion, nuevo);
            await db.SaveChangesAsync();
        }, Responder = (m, intento) => m.Fids.Select(_ => new ResultadoEnvioPush(
            intento == 1 ? EstadoEnvioPush.RegistroInvalido : EstadoEnvioPush.Aceptado, intento == 1 ? null : "nuevo-fid")).ToArray() };
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Equal(2, transporte.Enviados.Count);
        Assert.Equal(nuevo, Assert.Single(transporte.Enviados[1].Fids));
        await using var comprobar = p.Data.CreateDbContext();
        Assert.True((await comprobar.InstalacionesMoviles.SingleAsync()).Activa);
        Assert.Equal(2, await comprobar.EntregasPush.CountAsync());
    }

    [Fact]
    public async Task LecturaDeDestinosExigeReservaVigenteYRolWorker()
    {
        using var p = await PrepararPush(1);
        await using var services = await ServiciosWorker(p);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
        var outbox = new EfOutboxRepository(db);
        var procesador = Guid.NewGuid();
        var mensaje = await outbox.ReservarSiguienteAsync(procesador, OutboxDestino.PushFcm);
        Assert.Equal(p.Mensaje, mensaje!.Id);
        var repo = new EfDespachoPushRepository(db);
        var ajeno = await Assert.ThrowsAsync<PostgresException>(() => repo.PendientesAsync(p.Mensaje, Guid.NewGuid(), default));
        Assert.Equal("42501", ajeno.SqlState);
        Assert.Single((await repo.PendientesAsync(p.Mensaje, procesador, default)).Instalaciones);
        var directo = await Assert.ThrowsAsync<PostgresException>(() => db.InstalacionesMoviles.AsNoTracking().ToListAsync());
        Assert.Equal("42501", directo.SqlState);
        await using (var owner = p.Data.CreateDbContext())
            await owner.OutboxMessages.Where(x => x.Id == p.Mensaje).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservaHastaUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var vencido = await Assert.ThrowsAsync<PostgresException>(() => repo.PendientesAsync(p.Mensaje, procesador, default));
        Assert.Equal("42501", vencido.SqlState);
    }

    private async Task<PreparadoPush> PrepararPush(int cantidad, bool registrarAviso = true)
    {
        var data = await postgres.CreateDatabaseAsync();
        var settings = new Dictionary<string, string?> { ["Push:RegistrationEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using (var seed = new IntegrationApiFactory(data, settings: settings))
        using (seed.CreateHttpClient()) { }
        var runtime = await NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        settings["DatabaseInitialization:SeedData"] = "false";
        var factory = new IntegrationApiFactory(runtime, settings: settings);
        var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version });
        Assert.Equal(HttpStatusCode.OK, despacho.StatusCode);
        await using var db = data.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => x.Id == preparado.AccesoId);
        var email = await db.Usuarios.Where(x => x.Id == access.UsuarioId).Select(x => x.Email).SingleAsync();
        using var login = await client.PostAsJsonAsync("/api/auth/mobile/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var sesion = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sesion);
        var fids = Enumerable.Range(0, cantidad).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var fid in fids)
        {
            using var registro = await client.PutAsJsonAsync("/api/mobile/instalaciones", new { InstalacionId = Guid.CreateVersion7(), Plataforma = "Android", FirebaseInstallationId = fid, Permitida = true });
            Assert.Equal(HttpStatusCode.NoContent, registro.StatusCode);
        }
        var versionEnvio = await db.Envios.Where(e => e.Id == preparado.EnvioId).Select(e => e.Version).SingleAsync();
        var aviso = new AvisoReparto(access.OperadorId!.Value, access.Id, hoja.Id, preparado.EnvioId, versionEnvio, "Cancelacion", "Motivo de prueba", DateTimeOffset.UtcNow);
        var mensaje = OutboxMessage.ForShipment(aviso.OperadorId, aviso.EnvioId, OutboxMessageTypes.DeliveryChanged,
            OutboxDestino.PushFcm, JsonSerializer.Serialize(new { avisoId = aviso.Id }));
        if (registrarAviso) { db.AvisosReparto.Add(aviso); db.OutboxMessages.Add(mensaje); await db.SaveChangesAsync(); }
        return new(data, factory, client, aviso, mensaje.Id, fids, config);
    }

    private static async Task<ServiceProvider> ServiciosWorker(PreparadoPush p)
    {
        var rol = "push_test_" + Guid.NewGuid().ToString("N");
        var clave = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var conn = new NpgsqlConnection(p.Data.ConnectionString))
        {
            await conn.OpenAsync();
            await using var crear = new NpgsqlCommand($"CREATE ROLE {rol} LOGIN PASSWORD '{clave}' NOSUPERUSER NOBYPASSRLS; GRANT nexoruta_worker TO {rol}", conn);
            await crear.ExecuteNonQueryAsync();
        }
        var cs = new NpgsqlConnectionStringBuilder(p.Data.ConnectionString) { Username = rol, Password = clave }.ConnectionString;
        return new ServiceCollection().AddSingleton<IConfiguration>(p.Config)
            .AddDbContext<NexoRutaDbContext>(o => o.UseNexoRutaPostgres(cs))
            .AddScoped<EfOutboxRepository>().AddScoped<EfDespachoPushRepository>().AddScoped<CifradorRegistroPush>().BuildServiceProvider();
    }

    private sealed record PreparadoPush(IntegrationDatabase Data, IntegrationApiFactory Factory, HttpClient Client,
        AvisoReparto Aviso, Guid Mensaje, string[] Fids, IConfiguration Config) : IDisposable
    { public void Dispose() { Client.Dispose(); Factory.Dispose(); } }

    private sealed class Transporte : ITransporteFcm
    {
        public List<MulticastMessage> Enviados { get; } = [];
        public Func<MulticastMessage, int, IReadOnlyList<ResultadoEnvioPush>>? Responder { get; init; }
        public Func<int, Task>? AntesDeResponder { get; init; }
        public async Task<IReadOnlyList<ResultadoEnvioPush>> EnviarAsync(MulticastMessage mensaje, CancellationToken ct)
        {
            Enviados.Add(mensaje);
            if (AntesDeResponder is not null) await AntesDeResponder(Enviados.Count);
            return Responder?.Invoke(mensaje, Enviados.Count)
                ?? mensaje.Fids.Select(_ => new ResultadoEnvioPush(EstadoEnvioPush.Aceptado, "fcm-prueba")).ToArray();
        }
    }
}
