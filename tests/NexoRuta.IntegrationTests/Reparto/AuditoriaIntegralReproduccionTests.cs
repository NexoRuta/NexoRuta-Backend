using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;

namespace NexoRuta.IntegrationTests.Reparto;

// Review-only reproducer. It intentionally asserts the required outcome and is red
// on the audited baseline; do not weaken it or change product during this review.
public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task AuditoriaIntegral_InvalidPngMustNotValidateDelivery()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory())
        using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var factory = new IntegrationApiFactory(runtime, settings: settings);
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        await using var db = data.CreateDbContext();
        var envio = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parada.EnvioId);
        var reglas = await db.VersionesReglasOperativas.Include(r => r.RequisitosPrueba)
            .SingleAsync(r => r.Id == envio.VersionReglasId);
        Assert.Contains(reglas.RequisitosPrueba, r => r.Tipo == TipoPruebaEntrega.Fotografia);
        var entrega = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion));
        Assert.Equal("EntregaPendienteValidacion", entrega.EstadosEnvio[parada.EnvioId]);
        // Only a PNG signature and eight zero bytes: no IHDR, image data or IEND.
        // This is structurally not a decodable photograph, independent of product code.
        byte[] invalid = [137,80,78,71,13,10,26,10,0,0,0,0,0,0,0,0];
        var foto = Guid.NewGuid();
        using var upload = await client.PutAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{foto}",
            new { parada.EnvioId, Tipo = "Foto", Contenido = invalid });
        if (upload.StatusCode == HttpStatusCode.BadRequest) return; // Valid fix: reject malformed content at upload.
        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        var applied = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "EvidenciaEntrega", parada.EnvioId, entrega.VersionEnvio,
            new { FotoId = foto, Nombre = "Receptor sintético auditoría", Latitud = -34.9, Longitud = -56.2,
                PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow }));
        var actual = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parada.EnvioId);
        Assert.NotEqual(EstadoEnvio.Entregado, actual.Estado);
    }
}
