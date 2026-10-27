using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelacionConcurrenteNoDeshaceEntregaFisicaNiExigeRendirla(bool restringida)
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateApiFactory())
        using (seed.CreateHttpClient()) { }
        var runtime = restringida ? await RlsRestrictedHttpFlowTests.RestrictApiAsync(data) : data;
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            envio.CambiarEstado(EstadoEnvio.Cancelado, envio.Version);
            var ruta = await db.HojasRuta.Include(h => h.Paradas).SingleAsync(h => h.Id == hoja.Id);
            ruta.SuspenderParada(envio.Id, "Cancelación solicitada mientras el teléfono no tenía conexión.");
            await db.SaveChangesAsync();
        }
        var id = Guid.CreateVersion7();
        var comando = Operacion(id, 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion);
        var entrega = await Aplicar(client, hoja, comando);
        Assert.Equal("Aplicada", entrega.Resultado);
        Assert.Equal("Cancelado", entrega.EstadosEnvio[parada.EnvioId]);
        Assert.Equal(JsonSerializer.Serialize(entrega), JsonSerializer.Serialize(await Aplicar(client, hoja, comando)));
        var rendicion = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "Rendicion", datos: new { BultoIds = new[] { parada.Bultos[0].Id } }));
        Assert.Equal("Conflicto", rendicion.Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 6, "FinJornada"))).Resultado);
        await using var final = data.CreateDbContext();
        var entregado = await final.Envios.SingleAsync(e => e.Id == parada.EnvioId);
        Assert.Equal(EstadoEnvio.Cancelado, entregado.Estado);
        Assert.NotNull(entregado.EntregaFisicaEnUtc);
        var hecho = Assert.Single(await final.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.Hecho && x.OrigenTipo == "OperacionJornada" && x.OrigenId == id).ToArrayAsync());
        Assert.Equal((EstadoEnvio.Cancelado.ToString(), id, parada.EnvioVersion + 1),
            (hecho.EstadoObservado, hecho.OperacionJornadaId, hecho.VersionAnterior!.Value));
        Assert.Null(hecho.EstadoAnterior); Assert.Null(hecho.EstadoNuevo);
        Assert.DoesNotContain(await final.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado).ToArrayAsync(), x => x.EstadoAnterior == "Cancelado" && x.EstadoNuevo == "Cancelado");
        Assert.Equal(EstadoCustodiaBulto.Destinatario, (await final.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id)).Custodia);
        Assert.Single(await final.OperacionesJornada.Where(x => x.Id == id).ToArrayAsync());
        using var recepcion = await client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion", new { entregado.Version });
        Assert.Equal(HttpStatusCode.Conflict, recepcion.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConciliacionNoInventaEntregaDeUnBultoYaRendidoORecibido(bool recibido)
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            envio.CambiarEstado(EstadoEnvio.Cancelado, envio.Version);
            var bulto = await db.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id);
            bulto.DeclararRendicion(hoja.RepartidorAccesoId, hoja.Id);
            if (recibido) bulto.RecibirEnDeposito();
            await db.SaveChangesAsync();
        }
        Assert.Equal("Conflicto", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion))).Resultado);
        await using var final = data.CreateDbContext();
        var envioFinal = await final.Envios.SingleAsync(e => e.Id == parada.EnvioId);
        Assert.Equal(EstadoEnvio.Cancelado, envioFinal.Estado); Assert.Null(envioFinal.EntregaFisicaEnUtc);
        Assert.Empty(await final.PruebasEntrega.ToArrayAsync());
        Assert.Equal(recibido ? EstadoCustodiaBulto.Deposito : EstadoCustodiaBulto.PendienteDeposito,
            (await final.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id)).Custodia);
    }

    [Fact]
    public async Task CancelacionConcurrenteConRecepcionDeDevolucionNuncaReactivaCancelado()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            envio.CambiarEstado(EstadoEnvio.NoEntregado, envio.Version);
            envio.CambiarEstado(EstadoEnvio.EnDevolucion, envio.Version);
            var bulto = await db.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id);
            bulto.DeclararRendicion(hoja.RepartidorAccesoId, hoja.Id);
            await db.SaveChangesAsync();
        }

        var cancelar = client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/cancelacion",
            new { Version = parada.EnvioVersion + 2, Motivo = "Cancelación contra recepción." });
        var recibir = client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion", new { Version = parada.EnvioVersion + 2 });
        using var cancelacion = await cancelar;
        using var recepcion = await recibir;

        Assert.Contains(cancelacion.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Contains(recepcion.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
        await using var final = data.CreateDbContext();
        var envioFinal = await final.Envios.SingleAsync(e => e.Id == parada.EnvioId);
        Assert.Contains(envioFinal.Estado, new[] { EstadoEnvio.Cancelado, EstadoEnvio.Devuelto });
        Assert.Equal(cancelacion.IsSuccessStatusCode ? EstadoEnvio.Cancelado : EstadoEnvio.Devuelto, envioFinal.Estado);
        Assert.Equal(cancelacion.IsSuccessStatusCode ? 1 : 0, await final.CancelacionesEnvio.CountAsync());
        Assert.Equal(recepcion.StatusCode == HttpStatusCode.NoContent ? EstadoCustodiaBulto.Deposito : EstadoCustodiaBulto.PendienteDeposito,
            (await final.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id)).Custodia);
    }

    [Fact]
    public async Task CancelacionSinEntregaPermiteRendicionYRecepcionSinInventarEntrega()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            envio.CambiarEstado(EstadoEnvio.Cancelado, envio.Version); await db.SaveChangesAsync();
        }
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "Rendicion", datos: new { BultoIds = new[] { parada.Bultos[0].Id } }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "FinJornada"))).Resultado);
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            using var recibir = await client.PostAsJsonAsync($"/api/deposito/envios/{envio.Id}/recepcion", new { envio.Version });
            Assert.Equal(HttpStatusCode.NoContent, recibir.StatusCode);
        }
        await using var final = data.CreateDbContext();
        var cancelado = await final.Envios.SingleAsync(e => e.Id == parada.EnvioId);
        Assert.Equal(EstadoEnvio.Cancelado, cancelado.Estado); Assert.Null(cancelado.EntregaFisicaEnUtc);
        Assert.Equal(EstadoCustodiaBulto.Deposito, (await final.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id)).Custodia);
    }
}
