using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.ReglasOperativas;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task PublicarOtrasReglasNoCambiaMotivosIntervaloNiMaximoDelEnvioDescargado()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        await PublicarOtraPolitica(data);
        var remota = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        Assert.Equal(parada.Reglas.VersionId, remota.Paradas[0].Reglas.VersionId);
        Assert.Equal(3, remota.Paradas[0].Reglas.MaximoIntentos);
        Assert.Equal("AUSENTE", Assert.Single(remota.Paradas[0].Reglas.Motivos).Codigo);
        long? version = parada.EnvioVersion;
        async Task<ResultadoOperacionResponse> Intentar(long secuencia, int segundos, string motivo = "AUSENTE")
        {
            var respuesta = await Aplicar(client, hoja, new { Id = Guid.NewGuid(), Secuencia = secuencia, Tipo = "IntentoFallido",
                EnvioId = parada.EnvioId, VersionEnvio = version, OcurridaEn = LlegadaEstimada.AddSeconds(segundos), Datos = new { Motivo = motivo } });
            version = respuesta.VersionEnvio; return respuesta;
        }
        Assert.Equal("Conflicto", (await Intentar(4, 0, "Ausente")).Resultado);
        Assert.Equal("Aplicada", (await Intentar(5, 0)).Resultado);
        Assert.Equal("Conflicto", (await Intentar(6, 30)).Resultado);
        Assert.Equal("Conflicto", (await Intentar(7, 60)).Resultado);
        Assert.Equal("Conflicto", (await Intentar(8, 120)).Resultado);
        Assert.Equal("Conflicto", (await Intentar(9, 180)).Resultado);
        await using var db = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.NoEntregado, (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
        Assert.Equal(1, await db.OperacionesJornada.CountAsync(o => o.Tipo == "IntentoFallido" && o.Resultado == "Aplicada"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnviosNuevosUsanLaPublicacionParaValidarPruebaOIniciarDevolucion(bool intentoFallido)
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        await PublicarOtraPolitica(data);
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        Assert.Equal(1, parada.Reglas.MaximoIntentos); Assert.True(parada.Reglas.RequiereNombre); Assert.False(parada.Reglas.RequiereFoto);
        var respuesta = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, intentoFallido ? "IntentoFallido" : "EntregaFisica",
            parada.EnvioId, parada.EnvioVersion,
            new { Motivo = "RECHAZADO", Nombre = "Receptor de prueba", Latitud = -34.9, Longitud = -56.2,
                PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow }));
        Assert.Equal("Aplicada", respuesta.Resultado);
        await using (var db = data.CreateDbContext())
        {
            Assert.Equal(intentoFallido ? EstadoEnvio.EnDevolucion : EstadoEnvio.Entregado,
                (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
            Assert.Equal(intentoFallido ? EstadoCustodiaBulto.Repartidor : EstadoCustodiaBulto.Destinatario,
                (await db.Bultos.SingleAsync(b => b.EnvioId == parada.EnvioId)).Custodia);
        }
        if (intentoFallido)
        {
            Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "Rendicion",
                datos: new { BultoIds = parada.Bultos.Select(b => b.Id).ToArray() }))).Resultado);
            using var recepcion = await client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion", new { Version = respuesta.VersionEnvio });
            Assert.Equal(System.Net.HttpStatusCode.NoContent, recepcion.StatusCode);
            await using var db = data.CreateDbContext();
            Assert.Equal(EstadoEnvio.Devuelto, (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
        }
    }

    private static async Task PublicarOtraPolitica(IntegrationDatabase data)
    {
        await using var db = data.CreateDbContext();
        var operador = await db.AccesosUsuario.SingleAsync(a => a.OperadorId != null);
        var repo = new EfReglasOperativasRepository(db);
        await repo.CrearBorradorAsync(operador.OperadorId!.Value, operador.UsuarioId);
        await repo.EditarBorradorAsync(operador.OperadorId.Value, operador.UsuarioId, 0,
            new(1, 30, PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente, 2880, 1440, 60,
                [TipoPruebaEntrega.NombreReceptor], [new("AUSENTE", "Ausente", false), new("RECHAZADO", "Rechazado", true)]));
        await repo.PublicarBorradorAsync(operador.OperadorId.Value, operador.UsuarioId, 1);
    }
}
