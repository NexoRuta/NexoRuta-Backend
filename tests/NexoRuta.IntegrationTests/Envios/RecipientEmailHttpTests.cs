using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class RecipientEmailHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sin-correo")]
    [InlineData("Nombre <persona@example.test>")]
    public async Task AltaSinCorreoValidoNoPersisteEnvioNiOutbox(string? email)
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        await client.SetBearerForAccessAsync(data, comercio.Id);
        var body = new Dictionary<string, object?> { ["operadorId"] = (await db.Operadores.SingleAsync()).Id,
            ["destinatarioNombre"] = "Destinatario", ["direccion"] = "Dirección", ["codigoBulto"] = "EMAIL-TEST",
            ["pesoGramos"] = 100m, ["largoCentimetros"] = 1m, ["anchoCentimetros"] = 1m, ["altoCentimetros"] = 1m };
        if (email is not null) body["destinatarioEmail"] = email;
        using var respuesta = await client.PostAsJsonAsync("/api/envios", body);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Empty(await db.Envios.ToArrayAsync()); Assert.Empty(await db.Destinatarios.ToArrayAsync()); Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }

    [Fact]
    public async Task CorreoSePersisteYSoloExisteLaFirmaSqlActual()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null)).Id);
        using var respuesta = await client.PostAsJsonAsync("/api/envios", new { OperadorId = (await db.Operadores.SingleAsync()).Id,
            DestinatarioNombre = "Destinatario", DestinatarioEmail = "persona@example.test", Direccion = "Dirección", CodigoBulto = "EMAIL-TEST",
            PesoGramos = 100m, LargoCentimetros = 1m, AnchoCentimetros = 1m, AltoCentimetros = 1m });
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal("persona@example.test", (await db.Destinatarios.SingleAsync()).Email);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT to_regprocedure('nexoruta_app.create_shipment(uuid,uuid,uuid,uuid,text,text,text,numeric,numeric,numeric,numeric)') IS NULL AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task MigracionExigeCompletarCorreoRealYNoInventaValores()
    {
        var data = await postgres.CreateDatabaseAsync("20261019225246_AddRecipientEmail"); await using var db = data.CreateDbContext();
        var op = new Operador("Operador"); var destinatario = new Destinatario(op.Id, "Destinatario", "persona@example.test");
        db.AddRange(op, destinatario); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Destinatarios\" SET \"Email\"=NULL WHERE \"Id\"={destinatario.Id}");
        var migrator = db.GetService<IMigrator>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync()); Assert.Equal("23514", error.SqlState);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT \"Email\" IS NULL AS \"Value\" FROM \"Destinatarios\" WHERE \"Id\"={destinatario.Id}").SingleAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Destinatarios\" SET \"Email\"={destinatario.Email} WHERE \"Id\"={destinatario.Id}");
        await migrator.MigrateAsync();
        var requerido = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Destinatarios\" SET \"Email\"=NULL WHERE \"Id\"={destinatario.Id}"));
        Assert.Equal("23502", requerido.SqlState);
    }
}
