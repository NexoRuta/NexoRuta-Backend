using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.ReglasOperativas;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class ReglasEnvioAdmisionHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task SinPublicacionNoAdmiteEnviosYLaReferenciaPublicadaNoPuedeReemplazarse()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var seed = new IntegrationApiFactory(database, seedOperationalRules: false);
        using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var operador = await db.AccesosUsuario.SingleAsync(a => a.OperadorId != null);
        var comercio = await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null);
        await client.SetBearerForAccessAsync(database, comercio.Id);
        object Alta(string codigo) => new { OperadorId = operador.OperadorId, DestinatarioNombre = "Destinatario", DestinatarioEmail = "destinatario@example.test",
            Direccion = "Dirección de prueba", CodigoBulto = codigo, PesoGramos = 1000, LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10 };
        using var sinReglas = await client.PostAsJsonAsync("/api/envios", Alta("SIN-REGLAS"));
        Assert.Equal(HttpStatusCode.Conflict, sinReglas.StatusCode);
        Assert.Contains("publicar", await sinReglas.Content.ReadAsStringAsync());
        Assert.Empty(await db.Envios.ToArrayAsync()); Assert.Empty(await db.Destinatarios.ToArrayAsync()); Assert.Empty(await db.OutboxMessages.ToArrayAsync());
        var primeraVersion = await ReglasEnvioTestData.PublicarInicialAsync(db, operador.OperadorId!.Value, operador.UsuarioId);
        using var primerAlta = await client.PostAsJsonAsync("/api/envios", Alta("REGLAS-1"));
        Assert.Equal(HttpStatusCode.Created, primerAlta.StatusCode);
        var primero = (await primerAlta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var repo = new EfReglasOperativasRepository(db);
        var borrador = await repo.CrearBorradorAsync(operador.OperadorId.Value, operador.UsuarioId);
        await repo.EditarBorradorAsync(operador.OperadorId.Value, operador.UsuarioId, 0,
            new(5, 60, PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente, 4320, 2880, 60,
                [TipoPruebaEntrega.DocumentoReceptor], [new("AUSENTE", "Ausente", false), new("RECHAZADO", "Rechazado", true)]));
        await repo.PublicarBorradorAsync(operador.OperadorId.Value, operador.UsuarioId, 1);
        using var segundaAlta = await client.PostAsJsonAsync("/api/envios", Alta("REGLAS-2"));
        Assert.Equal(HttpStatusCode.Created, segundaAlta.StatusCode);
        var segundo = (await segundaAlta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var envios = await db.Envios.AsNoTracking().ToDictionaryAsync(e => e.Id);
        Assert.Equal(primeraVersion, envios[primero].VersionReglasId);
        Assert.Equal(borrador.Id, envios[segundo].VersionReglasId);
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Envios\" SET \"VersionReglasId\"={borrador.Id} WHERE \"Id\"={primero}"));
        Assert.Equal("55000", immutable.SqlState);
        Assert.Equal(primeraVersion, await db.Envios.Where(e => e.Id == primero).Select(e => e.VersionReglasId).SingleAsync());
    }
}
