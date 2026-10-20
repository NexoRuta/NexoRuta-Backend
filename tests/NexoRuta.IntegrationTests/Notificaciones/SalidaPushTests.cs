using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker;
namespace NexoRuta.IntegrationTests.Notificaciones;

public sealed partial class PushOutboxTests
{
    [Fact]
    public async Task BajaEsIdempotenteYNoDesactivaOtrosDispositivosDeLaCuenta()
    {
        using var p = await PrepararPush(2);
        Guid id;
        await using (var db = p.Data.CreateDbContext()) id = await db.InstalacionesMoviles.Select(i => i.Id).FirstAsync();
        for (var i = 0; i < 2; i++)
        {
            using var baja = await p.Client.DeleteAsync($"/api/mobile/instalaciones/{id}");
            Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);
        }
        await using var comprobar = p.Data.CreateDbContext();
        Assert.False((await comprobar.InstalacionesMoviles.SingleAsync(i => i.Id == id)).Activa);
        Assert.Equal(1, await comprobar.InstalacionesMoviles.CountAsync(i => i.Activa));
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Single(Assert.Single(transporte.Enviados).Fids);
    }

    [Theory]
    [InlineData("perfil")]
    [InlineData("permiso")]
    public async Task PuedeDarDeBajaSuInstalacionAunqueYaNoPuedaRepartir(string cambio)
    {
        using var p = await PrepararPush(1);
        Guid id;
        await using (var db = p.Data.CreateDbContext())
        {
            id = await db.InstalacionesMoviles.Select(i => i.Id).SingleAsync();
            if (cambio == "perfil") await db.Repartidores.ExecuteUpdateAsync(s => s.SetProperty(r => r.Activo, false));
            else await db.RolesPermisos.Where(rp => db.Permisos.Any(pm => pm.Id == rp.PermisoId && pm.Codigo == "reparto.ejecutar")).ExecuteDeleteAsync();
        }
        using var perfil = await p.Client.GetAsync("/api/mobile/perfil");
        Assert.Equal(HttpStatusCode.Forbidden, perfil.StatusCode);
        using var baja = await p.Client.DeleteAsync($"/api/mobile/instalaciones/{id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);
        await using var verificar = p.Data.CreateDbContext();
        Assert.False((await verificar.InstalacionesMoviles.SingleAsync()).Activa);
    }

    [Fact]
    public async Task OtroRepartidorNoDesactivaInstalacionAjenaYNingunJwtWebPuedeDarlaDeBaja()
    {
        using var p = await PrepararPush(1);
        Guid id;
        await using (var db = p.Data.CreateDbContext()) id = await db.InstalacionesMoviles.Select(i => i.Id).SingleAsync();
        var futuro = await CrearDestino(p);
        string email;
        await using (var db = p.Data.CreateDbContext())
            email = await db.AccesosUsuario.Where(a => a.Id == futuro.RepartidorAccesoId)
                .Join(db.Usuarios, a => a.UsuarioId, u => u.Id, (a, u) => u.Email).SingleAsync();
        using var login = await p.Client.PostAsJsonAsync("/api/auth/mobile/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        p.Client.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        using var ajena = await p.Client.DeleteAsync($"/api/mobile/instalaciones/{id}");
        Assert.Equal(HttpStatusCode.NotFound, ajena.StatusCode);
        await p.Client.SetBearerForAccessAsync(p.Data, p.Aviso.AccesoId);
        using var web = await p.Client.DeleteAsync($"/api/mobile/instalaciones/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, web.StatusCode);
        await using var verificar = p.Data.CreateDbContext();
        Assert.True((await verificar.InstalacionesMoviles.SingleAsync()).Activa);
    }

    [Fact]
    public async Task SalidaTrasReingresoDaDeBajaLaInstalacionVinculadaALaSesionAnterior()
    {
        using var p = await PrepararPush(1);
        Guid id, sesionAnterior; string email;
        await using (var db = p.Data.CreateDbContext())
        {
            var instalacion = await db.InstalacionesMoviles.SingleAsync(); id = instalacion.Id; sesionAnterior = instalacion.SesionId;
            email = await db.AccesosUsuario.Where(a => a.Id == p.Aviso.AccesoId)
                .Join(db.Usuarios, a => a.UsuarioId, u => u.Id, (a, u) => u.Email).SingleAsync();
        }
        using var login = await p.Client.PostAsJsonAsync("/api/auth/mobile/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(sesionAnterior, tokens.GetProperty("sesionId").GetGuid());
        p.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        using var baja = await p.Client.DeleteAsync($"/api/mobile/instalaciones/{id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);
        using var salida = await p.Client.PostAsJsonAsync("/api/auth/mobile/logout", new { RefreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.NoContent, salida.StatusCode);
        using var yaRevocada = await p.Client.GetAsync("/api/mobile/perfil");
        Assert.Equal(HttpStatusCode.Unauthorized, yaRevocada.StatusCode);
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default)); Assert.Empty(transporte.Enviados);
    }
}
