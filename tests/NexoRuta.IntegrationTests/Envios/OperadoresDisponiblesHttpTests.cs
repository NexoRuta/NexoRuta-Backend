using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class OperadoresDisponiblesHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("api/operadores")]
    [InlineData("api/comercio/operadores")]
    public async Task CatalogoGlobal_ConservaAmbasRutasYAutorizaSoloComercios(string route)
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await new DatosInicialesSeeder(db,
            new AccesoInicial("owner@example.test", "Alpha operator", "Test commerce", "operator@example.test"))
            .SeedAsync();
        db.Operadores.Add(new Operador("Beta operator without users"));
        await db.SaveChangesAsync();
        var comercio = await db.AccesosUsuario.Where(x => x.ComercioId != null).SingleAsync();
        var operador = await db.AccesosUsuario.Where(x => x.OperadorId != null).SingleAsync();

        using var api = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        {
            ["DatabaseInitialization:SeedData"] = "false"
        });
        using var client = api.CreateHttpClient();
        using var anonymous = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var operatorRequest = new HttpRequestMessage(HttpMethod.Get, route);
        operatorRequest.Headers.Add("X-NexoRuta-Acceso", operador.Id.ToString());
        using var forbidden = await client.SendAsync(operatorRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var commerceRequest = new HttpRequestMessage(HttpMethod.Get, route);
        commerceRequest.Headers.Add("X-NexoRuta-Acceso", comercio.Id.ToString());
        using var available = await client.SendAsync(commerceRequest);
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        var catalog = await available.Content.ReadFromJsonAsync<OperadorDisponible[]>();
        Assert.NotNull(catalog);
        Assert.Equal(new[] { "Alpha operator", "Beta operator without users" }, catalog.Select(x => x.Nombre));
        Assert.All(catalog, item => Assert.NotEqual(Guid.Empty, item.OperadorId));
    }
}
