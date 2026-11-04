using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    [Theory]
    [InlineData("Foto", false)]
    [InlineData("Firma", false)]
    [InlineData("Foto", true)]
    [InlineData("Firma", true)]
    public async Task EvidenciaDecodificadaRechazaCorrupcionSinEscriturasYConservaCifrado(string tipo, bool jpeg)
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory())
        using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var key = RandomNumberGenerator.GetBytes(32);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(key);
        using var factory = new IntegrationApiFactory(runtime, settings: settings);
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var id = Guid.NewGuid();
        var uri = $"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{id}";
        var valid = jpeg ? Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD50ooor8MP9Uz/2Q==") : FotoPrueba;
        await using var db = data.CreateDbContext();
        var before = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parada.EnvioId);
        var outbox = await db.OutboxMessages.CountAsync();
        using var invalid = await client.PutAsJsonAsync(uri, new { parada.EnvioId, Tipo = tipo, Contenido = valid[..(valid.Length / 2)] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(await db.EvidenciasReparto.ToArrayAsync());
        var after = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parada.EnvioId);
        Assert.Equal(before.Estado, after.Estado);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(outbox, await db.OutboxMessages.CountAsync());
        using var upload = await client.PutAsJsonAsync(uri, new { parada.EnvioId, Tipo = tipo, Contenido = valid });
        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        using var retry = await client.PutAsJsonAsync(uri, new { parada.EnvioId, Tipo = tipo, Contenido = valid });
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        var stored = Assert.Single(await db.EvidenciasReparto.AsNoTracking().ToArrayAsync());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(valid)), stored.Hash);
        Assert.False(valid.SequenceEqual(stored.Cifrado));
        var plaintext = new byte[stored.Cifrado.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(stored.Nonce, stored.Cifrado, stored.Tag, plaintext,
            Encoding.UTF8.GetBytes($"{stored.OperadorId:N}:{stored.RutaId:N}:{stored.EnvioId:N}:{stored.Id:N}:{stored.Tipo}:{stored.Hash}"));
        Assert.Equal(valid, plaintext);
        using var context = await client.PutAsJsonAsync(uri, new { EnvioId = Guid.NewGuid(), Tipo = tipo, Contenido = valid });
        Assert.Equal(HttpStatusCode.NotFound, context.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await client.PutAsJsonAsync(uri, new { parada.EnvioId, Tipo = tipo, Contenido = valid });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
