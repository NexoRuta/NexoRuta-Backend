using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class AlternativasOperadorHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/operadores/alternativas-envio";
    private static string At(double lat = -34.901, double lon = -56.201, Guid? exclude = null)
        => FormattableString.Invariant($"{Route}?latitud={lat:R}&longitud={lon:R}&excluirOperadorId={exclude}");

    [Fact]
    public async Task OperatorCreatedAndPublishedAfterCatalogLoadAppearsWithoutReloadOrContextLeak()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        await using var db = data.CreateDbContext();
        var original = await db.Operadores.Select(o => o.Id).SingleAsync();
        var user = await db.AccesosUsuario.Where(a => a.OperadorId != null).Select(a => a.UsuarioId).SingleAsync();
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        await client.SetBearerForAccessAsync(data, await db.AccesosUsuario.Where(a => a.ComercioId != null).Select(a => a.Id).SingleAsync());
        using var initial = await client.GetAsync("/api/operadores");
        initial.EnsureSuccessStatusCode();
        var op = new Operador("Created after form load"); db.Operadores.Add(op); await db.SaveChangesAsync();
        Assert.DoesNotContain(op.Id.ToString(), await initial.Content.ReadAsStringAsync());
        await ServiciosEnvioTestData.PublicarServicioAsync(db, op.Id, user);
        using var beforeRules = await client.GetAsync(At(exclude: original));
        Assert.DoesNotContain((await beforeRules.Content.ReadFromJsonAsync<JsonElement[]>())!, r => r.GetProperty("operadorId").GetGuid() == op.Id);
        await ReglasEnvioTestData.PublicarInicialAsync(db, op.Id, user);
        for (var i = 0; i < 2; i++)
        {
            using var direct = await client.GetAsync($"/api/operadores/{op.Id}/servicios-envio?latitud=-34.901&longitud=-56.201");
            direct.EnsureSuccessStatusCode(); Assert.NotEmpty((await direct.Content.ReadFromJsonAsync<JsonElement[]>())!);
            using var alternatives = await client.GetAsync(At(exclude: original));
            alternatives.EnsureSuccessStatusCode();
            Assert.Contains((await alternatives.Content.ReadFromJsonAsync<JsonElement[]>())!, r => r.GetProperty("operadorId").GetGuid() == op.Id);
            using var excluded = await client.GetAsync(At(exclude: op.Id));
            Assert.DoesNotContain((await excluded.Content.ReadFromJsonAsync<JsonElement[]>())!, r => r.GetProperty("operadorId").GetGuid() == op.Id);
        }
    }

    [Fact]
    public async Task QuoteAndCreateRequireOperationalPublicationEvenWhenTariffExists()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        await using var db = data.CreateDbContext();
        var op = new Operador("Published tariff without operational rules"); db.Operadores.Add(op); await db.SaveChangesAsync();
        await ServiciosEnvioTestData.PublicarServicioAsync(db, op.Id, await db.AccesosUsuario.Where(a => a.OperadorId != null).Select(a => a.UsuarioId).SingleAsync());
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        await client.SetBearerForAccessAsync(data, await db.AccesosUsuario.Where(a => a.ComercioId != null).Select(a => a.Id).SingleAsync());
        using var alternatives = await client.GetAsync(At());
        Assert.DoesNotContain((await alternatives.Content.ReadFromJsonAsync<JsonElement[]>())!, item => item.GetProperty("operadorId").GetGuid() == op.Id);
        using var direct = await client.GetAsync($"/api/operadores/{op.Id}/servicios-envio?latitud=-34.901&longitud=-56.201");
        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        Assert.Contains("reglas operativas", await direct.Content.ReadAsStringAsync());
        var request = new { OperadorId = op.Id, Servicio = ServiciosEnvioTestData.Declarar(op.Id), DestinatarioNombre = "Synthetic", DestinatarioEmail = "synthetic@example.test", Direccion = "Synthetic destination", Bultos = new[] { new { Codigo = "ALT-1", PesoGramos = 100, LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10 } } };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/envios/cotizacion", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/envios", request)).StatusCode);
        Assert.Empty(await db.Envios.ToArrayAsync()); Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }

    [Fact]
    public async Task ExactPointReturnsOnlyActiveContractablePublishedServicesAndMinimalData()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        await using var db = data.CreateDbContext();
        var original = await db.Operadores.Select(o => o.Id).SingleAsync();
        var originalZone = await db.ZonasCobertura.SingleAsync(z => z.OperadorId == original);
        originalZone.Desactivar(originalZone.Version); await db.SaveChangesAsync();
        var user = await db.AccesosUsuario.Where(a => a.OperadorId != null).Select(a => a.UsuarioId).SingleAsync();
        async Task<Guid> Add(string name, bool rules = true)
        {
            var op = new Operador(name); db.Operadores.Add(op); await db.SaveChangesAsync();
            if (rules) await ReglasEnvioTestData.PublicarInicialAsync(db, op.Id, user);
            await ServiciosEnvioTestData.PublicarServicioAsync(db, op.Id, user, incluirUrgente: name == "Available alternative");
            return op.Id;
        }
        var alternative = await Add("Available alternative");
        var suspended = await Add("Suspended");
        (await db.Operadores.SingleAsync(o => o.Id == suspended)).CambiarEstado(EstadoOrganizacion.Suspendida);
        var inactiveZone = await Add("Inactive zone");
        var zone = await db.ZonasCobertura.SingleAsync(z => z.OperadorId == inactiveZone); zone.Desactivar(zone.Version);
        await Add("No operational publication", rules: false);
        var draftOnly = new Operador("Draft tariff only"); db.Operadores.Add(draftOnly); await db.SaveChangesAsync();
        await ReglasEnvioTestData.PublicarInicialAsync(db, draftOnly.Id, user);
        db.TarifariosOperador.Add(TarifarioOperador.Crear(draftOnly.Id));
        db.VersionesTarifario.Add(VersionTarifario.CrearBorrador(draftOnly.Id, user));
        await db.SaveChangesAsync();
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        await client.SetBearerForAccessAsync(data, await db.AccesosUsuario.Where(a => a.ComercioId != null).Select(a => a.Id).SingleAsync());
        using var unavailableSelected = await client.GetAsync($"/api/operadores/{original}/servicios-envio?latitud=-34.901&longitud=-56.201");
        Assert.Empty((await unavailableSelected.Content.ReadFromJsonAsync<JsonElement[]>())!);
        using var response = await client.GetAsync(At(exclude: original));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var row = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
        Assert.Equal(alternative, row.GetProperty("operadorId").GetGuid());
        Assert.Equal("Available alternative", row.GetProperty("nombre").GetString());
        Assert.Equal(new[] { "Estandar", "Urgente" }, row.GetProperty("modalidades").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(new[] { "modalidades", "nombre", "operadorId" }, row.EnumerateObject().Select(p => p.Name).Order());
        using var boundary = await client.GetAsync(At(-35, -56.4, original));
        Assert.Single((await boundary.Content.ReadFromJsonAsync<JsonElement[]>())!);
        using var outside = await client.GetAsync(At(-35.000001, -56.4, original));
        Assert.Empty((await outside.Content.ReadFromJsonAsync<JsonElement[]>())!);
        var request = new { OperadorId = alternative, Servicio = ServiciosEnvioTestData.Declarar(alternative), DestinatarioNombre = "Synthetic", DestinatarioEmail = "synthetic@example.test", Direccion = "Synthetic destination", Bultos = new[] { new { Codigo = "ALT-1", PesoGramos = 100, LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10 } } };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/envios/cotizacion", request)).StatusCode);
        var advertised = await db.ZonasCobertura.SingleAsync(z => z.OperadorId == alternative); advertised.Desactivar(advertised.Version); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/envios/cotizacion", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/envios", request)).StatusCode);
        Assert.Empty(await db.Envios.ToArrayAsync());
    }

    [Fact]
    public async Task AuthorizationMissingCoordinatesAndInvalidCoordinatesFailClosed()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var catalogCalls = 0;
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings(),
            configureTestServices: services => services.AddScoped<IOperadoresRepository>(sp => new DisappearingCatalog(new EfOperadoresRepository(sp.GetRequiredService<NexoRutaDbContext>()), () => { catalogCalls++; return Task.CompletedTask; })));
        using var client = factory.CreateHttpClient(); await using var db = data.CreateDbContext();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(At())).StatusCode);
        await client.SetBearerForAccessAsync(data, await db.AccesosUsuario.Where(a => a.OperadorId != null).Select(a => a.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(At())).StatusCode);
        var commerce = await db.AccesosUsuario.Where(a => a.ComercioId != null).SingleAsync();
        await client.SetBearerForAccessAsync(data, commerce.Id);
        foreach (var query in new[] { "", "?latitud=-34.9", "?longitud=-56.2", "?latitud=NaN&longitud=-56", "?latitud=Infinity&longitud=-56", "?latitud=91&longitud=-56", "?latitud=-34&longitud=181" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route + query)).StatusCode);
        Assert.Equal(0, catalogCalls);
        var permission = await db.Permisos.SingleAsync(p => p.Codigo == "envios.crear");
        db.RolesPermisos.Remove(await db.RolesPermisos.SingleAsync(r => r.RolId == commerce.RolId && r.PermisoId == permission.Id)); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(At())).StatusCode);
    }

    [Fact]
    public async Task DisappearingCandidateFailsWholeRequestAndCannotBeAdmitted()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        await using var db = data.CreateDbContext();
        var disappearing = new Operador("Z disappearing after an eligible candidate"); db.Operadores.Add(disappearing); await db.SaveChangesAsync();
        var op = disappearing.Id;
        await ServiciosEnvioTestData.PublicarReglasYServicioAsync(db, op, await db.AccesosUsuario.Where(a => a.OperadorId != null).Select(a => a.UsuarioId).SingleAsync());
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings(),
            configureTestServices: services => services.AddScoped<IOperadoresRepository>(sp => new DisappearingCatalog(new EfOperadoresRepository(sp.GetRequiredService<NexoRutaDbContext>()), async () => {
                await using var admin = data.CreateDbContext();
                (await admin.Operadores.SingleAsync(o => o.Id == op)).CambiarEstado(EstadoOrganizacion.Suspendida); await admin.SaveChangesAsync();
            })));
        using var client = factory.CreateHttpClient();
        await client.SetBearerForAccessAsync(data, await db.AccesosUsuario.Where(a => a.ComercioId != null).Select(a => a.Id).SingleAsync());
        using var response = await client.GetAsync(At());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("operator_alternatives_changed", problem.GetProperty("code").GetString());
        Assert.Contains("intentá nuevamente", problem.GetProperty("detail").GetString());
        var request = new { OperadorId = op, Servicio = ServiciosEnvioTestData.Declarar(op), DestinatarioNombre = "Synthetic", DestinatarioEmail = "synthetic@example.test", Direccion = "Synthetic destination", Bultos = new[] { new { Codigo = "ALT-1", PesoGramos = 100, LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10 } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/envios/cotizacion", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/envios", request)).StatusCode);
        Assert.Empty(await db.Envios.ToArrayAsync()); Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }

    private sealed class DisappearingCatalog(IOperadoresRepository inner, Func<Task> disappear) : IOperadoresRepository
    {
        public async Task<IReadOnlyList<OperadorDisponible>> ListarAsync(CancellationToken ct = default) { var rows = await inner.ListarAsync(ct); await disappear(); return rows; }
        public Task<OperadorDisponible?> ObtenerAsync(Guid id, CancellationToken ct = default) => inner.ObtenerAsync(id, ct);
        public Task<IReadOnlyList<OperadorDisponibleParaImportacion>> ListarParaImportacionAsync(CancellationToken ct = default) => inner.ListarParaImportacionAsync(ct);
    }
}
