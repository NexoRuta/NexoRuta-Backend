using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class OperadoresDisponiblesHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task SuspendedOperatorsAreHiddenAndManualSelectionCannotCreateShipment()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(db,
                new AccesoInicial("owner@example.test", "Active operator", "Test commerce", "operator@example.test"))
                .SeedAsync();
            var suspended = new Operador("Suspended operator");
            suspended.CambiarEstado(EstadoOrganizacion.Suspendida);
            db.Operadores.Add(suspended);
            await db.SaveChangesAsync();
        }
        using var api = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        {
            ["DatabaseInitialization:SeedData"] = "false"
        });
        using var client = api.CreateHttpClient();
        var commerceToken = await LoginAsync(client, "owner@example.test");
        using var catalogRequest = new HttpRequestMessage(HttpMethod.Get, "/api/operadores");
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
        using var catalogResponse = await client.SendAsync(catalogRequest);
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<OperadorDisponible[]>();
        Assert.Equal(new[] { "Active operator" }, catalog!.Select(x => x.Nombre));

        Guid suspendedId;
        await using (var db = database.CreateDbContext())
            suspendedId = await db.Operadores.Where(x => x.Estado == EstadoOrganizacion.Suspendida).Select(x => x.Id).SingleAsync();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/envios")
        {
            Content = JsonContent.Create(new
            {
                OperadorId = suspendedId, DestinatarioNombre = "Ana", DestinatarioEmail = "destinatario@example.test", Direccion = "Rivera 123", CodigoBulto = "B-001",
                PesoGramos = 100m, LargoCentimetros = 10m, AnchoCentimetros = 10m, AltoCentimetros = 10m
            })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
        using var rejected = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.Envios.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task CatalogoGlobal_UsaUnaSolaRutaYAutorizaSoloComercios()
    {
        const string route = "api/operadores";
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await AuthenticationTestData.Seeder(db,
            new AccesoInicial("owner@example.test", "Alpha operator", "Test commerce", "operator@example.test"))
            .SeedAsync();
        db.Operadores.Add(new Operador("Beta operator without users"));
        await db.SaveChangesAsync();
        var comercio = await db.AccesosUsuario.Where(x => x.ComercioId != null).SingleAsync();
        var operador = await db.AccesosUsuario.Where(x => x.OperadorId != null).SingleAsync();

        using var api = new IntegrationApiFactory(database, "Development", settings: new Dictionary<string, string?>
        {
            ["DatabaseInitialization:SeedData"] = "false"
        });
        using var client = api.CreateHttpClient();
        using var anonymous = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var eliminada = await client.GetAsync("api/comercio/operadores");
        Assert.Equal(HttpStatusCode.NotFound, eliminada.StatusCode);

        var operatorEmail = (await db.Usuarios.SingleAsync(x => x.Id == operador.UsuarioId)).Email;
        var commerceEmail = (await db.Usuarios.SingleAsync(x => x.Id == comercio.UsuarioId)).Email;
        var operatorToken = await LoginAsync(client, operatorEmail);
        var commerceToken = await LoginAsync(client, commerceEmail);

        using var operatorRequest = new HttpRequestMessage(HttpMethod.Get, route);
        operatorRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        using var forbidden = await client.SendAsync(operatorRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var commerceRequest = new HttpRequestMessage(HttpMethod.Get, route);
        commerceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
        using var available = await client.SendAsync(commerceRequest);
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        var catalog = await available.Content.ReadFromJsonAsync<OperadorDisponible[]>();
        Assert.NotNull(catalog);
        Assert.Equal(new[] { "Alpha operator", "Beta operator without users" }, catalog.Select(x => x.Nombre));
        Assert.All(catalog, item => Assert.NotEqual(Guid.Empty, item.OperadorId));
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
}
