using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed class RepartoRestrictedHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RolRealApiPuedePrepararDespacharYEntregarSinPermisosDePropietarioDeBase()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var ownerFactory = data.CreateApiFactory();
        using var ownerClient = ownerFactory.CreateHttpClient();
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version });
        Assert.Equal(HttpStatusCode.OK, despacho.StatusCode);
        hoja = (await despacho.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        await using var db = data.CreateDbContext();
        var email = await db.Usuarios.Where(u => db.AccesosUsuario.Any(a => a.Id == preparado.AccesoId && a.UsuarioId == u.Id))
            .Select(u => u.Email).SingleAsync();
        using var login = await client.PostAsJsonAsync("/api/auth/mobile/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var sesion = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sesion);
        using var perfil = await client.GetAsync("/api/mobile/perfil");
        Assert.Equal(HttpStatusCode.OK, perfil.StatusCode);
        var parada = hoja.Paradas[0];
        var comandos = new[]
        {
            new { Tipo = "InicioJornada", Datos = (object)new { }, Envio = (Guid?)null },
            new { Tipo = "CargarBulto", Datos = (object)new { BultoId = parada.Bultos[0].Id }, Envio = (Guid?)parada.EnvioId },
            new { Tipo = "ConfirmarCarga", Datos = (object)new { }, Envio = (Guid?)null },
            new { Tipo = "EntregaFisica", Datos = (object)new { }, Envio = (Guid?)parada.EnvioId },
            new { Tipo = "FinJornada", Datos = (object)new { }, Envio = (Guid?)null }
        };
        for (var i = 0; i < comandos.Length; i++)
        {
            var comando = comandos[i];
            var version = await db.Envios.AsNoTracking().Where(e => e.Id == parada.EnvioId).Select(e => e.Version).SingleAsync();
            using var respuesta = await client.PostAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/operaciones",
                new { Id = Guid.CreateVersion7(), Secuencia = i + 1, comando.Tipo, EnvioId = comando.Envio,
                    VersionEnvio = version, OcurridaEn = DateTimeOffset.UtcNow, comando.Datos });
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
            Assert.Equal("Aplicada", (await respuesta.Content.ReadFromJsonAsync<ResultadoOperacionResponse>())!.Resultado);
        }
    }
}
