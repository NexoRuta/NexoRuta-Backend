using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Envios;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.ReglasOperativas;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task SegundoIntentoConNuevaIdentidadNoPuedeRepetirseEnLaParadaFallida()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (var seed = bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        await using var db = data.CreateDbContext();

        var intentoOriginal = new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        };
        var primera = await Aplicar(client, hoja, intentoOriginal);
        Assert.Equal("Aplicada", primera.Resultado);
        var historiaAntesReplay = await db.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado);
        var outboxAntesReplay = await db.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId);
        var repetida = await Aplicar(client, hoja, intentoOriginal);
        Assert.Equal("Aplicada", repetida.Resultado);
        Assert.Equal(primera.VersionEnvio, repetida.VersionEnvio);
        Assert.Equal(historiaAntesReplay, await db.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado));
        Assert.Equal(outboxAntesReplay, await db.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId));

        var segunda = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 5, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = primera.VersionEnvio, OcurridaEn = LlegadaEstimada.AddMinutes(30),
            Datos = new { Motivo = "AUSENTE" }
        });

        Assert.Equal("Conflicto", segunda.Resultado);
        var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        Assert.Equal("NoEntregado", envio.Estado.ToString());
        Assert.Equal(primera.VersionEnvio, envio.Version);
        Assert.Equal(1, await db.OperacionesJornada.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada"));
    }

    [Fact]
    public async Task ReprogramacionExplicitaEsAtomicamenteVersionadaYNoMueveLaCustodia()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (var seed = bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", fallido.Resultado);

        var manana = hoja.Fecha.AddDays(1);
        using var rutaDestinoResponse = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = hoja.RepartidorAccesoId,
            VehiculoId = hoja.VehiculoId,
            Fecha = manana,
            Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, rutaDestinoResponse.StatusCode);
        var rutaDestino = (await rutaDestinoResponse.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var reasignacionDirecta = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reasignacion", new
        {
            RutaDestinoId = rutaDestino.Id,
            VersionEnvio = fallido.VersionEnvio,
            VersionRutaDestino = rutaDestino.Version,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1),
            Motivo = "No debe omitir la reprogramación explícita"
        });
        Assert.Equal(HttpStatusCode.Conflict, reasignacionDirecta.StatusCode);

        var commerceAccessId = await AccessId(data, comercio: true);
        await client.SetBearerForAccessAsync(data, commerceAccessId);
        using var noAutorizada = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion",
            new { VersionEnvio = fallido.VersionEnvio });
        Assert.Equal(HttpStatusCode.Forbidden, noAutorizada.StatusCode);
        await client.SetBearerForAccessAsync(data, hoja.RepartidorAccesoId);

        int avisosAntes;
        int eventosAntes;
        await using (var db = data.CreateDbContext())
        {
            avisosAntes = await db.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId
                && x.Tipo == OutboxMessageTypes.DeliveryChanged);
            eventosAntes = await db.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
                && x.Tipo == TipoEventoEnvio.TransicionEstado);
        }

        var respuestasConcurrentes = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion", new { VersionEnvio = fallido.VersionEnvio }),
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion", new { VersionEnvio = fallido.VersionEnvio }));
        var respuesta = Assert.Single(respuestasConcurrentes, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, Assert.Single(respuestasConcurrentes, x => x.StatusCode == HttpStatusCode.Conflict).StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ReprogramacionEnvioResponse>())!;
        foreach (var concurrente in respuestasConcurrentes) concurrente.Dispose();
        Assert.Equal(parada.EnvioId, resultado.EnvioId);
        Assert.Equal(1, resultado.IntentosFallidos);
        Assert.Equal(3, resultado.MaximoIntentos);
        Assert.Equal(LlegadaEstimada.AddMinutes(30), resultado.ProximoIntentoElegibleUtc);
        Assert.Equal(fallido.VersionEnvio + 1, resultado.VersionEnvio);

        var viejaRuta = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        Assert.False(Assert.Single(viejaRuta.Paradas).Activa);
        Assert.Equal("Reprogramado", viejaRuta.Paradas[0].Estado);
        Assert.Equal(1, viejaRuta.Paradas[0].IntentosFallidos);
        Assert.Equal(LlegadaEstimada.AddMinutes(30), viejaRuta.Paradas[0].ProximoIntentoElegibleUtc);

        var operacionAnterior = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 5, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = resultado.VersionEnvio, OcurridaEn = LlegadaEstimada.AddMinutes(30),
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Conflicto", operacionAnterior.Resultado);

        await using var verificacion = data.CreateDbContext();
        var envio = await verificacion.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        Assert.Equal(EstadoEnvio.Reprogramado, envio.Estado);
        Assert.Equal(resultado.VersionEnvio, envio.Version);
        var bulto = await verificacion.Bultos.SingleAsync(x => x.EnvioId == parada.EnvioId);
        Assert.Equal(EstadoCustodiaBulto.Repartidor, bulto.Custodia);
        Assert.Equal(hoja.Id, bulto.RutaCustodiaId);
        var transicion = await verificacion.EnvioEventos.SingleAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado && x.EstadoAnterior == "NoEntregado"
            && x.EstadoNuevo == "Reprogramado");
        Assert.Equal("ReprogramacionEnvio", transicion.OrigenTipo);
        Assert.NotNull(transicion.CorrelacionId);
        Assert.Equal(1, await verificacion.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado) - eventosAntes);
        Assert.Equal(avisosAntes + 1, await verificacion.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == OutboxMessageTypes.DeliveryChanged));
        Assert.Equal(1, await verificacion.OperacionesJornada.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada"));
    }

    [Fact]
    public async Task ReprogramacionConcurrenteConCancelacionNoSobrescribeElEstadoGanador()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (var seed = bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        int transicionesAntes;
        await using (var db = data.CreateDbContext())
            transicionesAntes = await db.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
                && x.Tipo == TipoEventoEnvio.TransicionEstado);
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", fallido.Resultado);

        var operaciones = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion", new { VersionEnvio = fallido.VersionEnvio }),
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/cancelacion", new { Version = fallido.VersionEnvio, Motivo = "Cancelación concurrente de auditoría" }));
        try
        {
            var reprogramacion = Assert.Single(operaciones, x => x.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/reprogramacion") == true);
            var cancelacion = Assert.Single(operaciones, x => x.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/cancelacion") == true);
            Assert.True(reprogramacion.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
            Assert.True(cancelacion.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
            Assert.NotEqual(reprogramacion.IsSuccessStatusCode, cancelacion.IsSuccessStatusCode);

            await using var db = data.CreateDbContext();
            var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
            Assert.Equal(cancelacion.IsSuccessStatusCode ? EstadoEnvio.Cancelado : EstadoEnvio.Reprogramado, envio.Estado);
            Assert.Equal(transicionesAntes + 2, await db.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
                && x.Tipo == TipoEventoEnvio.TransicionEstado));
            Assert.Equal(cancelacion.IsSuccessStatusCode ? 1 : 0,
                await db.CancelacionesEnvio.CountAsync(x => x.EnvioId == parada.EnvioId));
        }
        finally
        {
            foreach (var operacion in operaciones) operacion.Dispose();
        }
    }

    [Fact]
    public async Task ReprogramacionConcurrenteConPlanificacionNoCreaAsignacionDuplicada()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (var seed = bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", fallido.Resultado);
        var manana = hoja.Fecha.AddDays(1);

        var operaciones = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion", new { VersionEnvio = fallido.VersionEnvio }),
            client.PostAsJsonAsync("/api/rutas", new
            {
                RepartidorAccesoId = hoja.RepartidorAccesoId,
                VehiculoId = hoja.VehiculoId,
                Fecha = manana,
                Paradas = new[] { new { EnvioId = parada.EnvioId, LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1) } }
            }));
        try
        {
            var reprogramacion = Assert.Single(operaciones, x => x.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/reprogramacion") == true);
            var planificacion = Assert.Single(operaciones, x => x.RequestMessage?.RequestUri?.AbsolutePath == "/api/rutas");
            Assert.Equal(HttpStatusCode.OK, reprogramacion.StatusCode);
            Assert.True(planificacion.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);

            await using var db = data.CreateDbContext();
            var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
            var paradasActivas = await db.ParadasRuta.CountAsync(x => x.EnvioId == parada.EnvioId && x.Activa);
            if (planificacion.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(EstadoEnvio.AsignadoRuta, envio.Estado);
                Assert.Equal(1, paradasActivas);
            }
            else
            {
                Assert.Equal(EstadoEnvio.Reprogramado, envio.Estado);
                Assert.Equal(0, paradasActivas);
            }
            Assert.Equal(1, await db.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
                && x.Tipo == TipoEventoEnvio.TransicionEstado && x.EstadoAnterior == "NoEntregado"
                && x.EstadoNuevo == "Reprogramado"));
        }
        finally
        {
            foreach (var operacion in operaciones) operacion.Dispose();
        }
    }

    [Fact]
    public async Task FalloAlRegistrarHistoriaRevierteEstadoParadaYOutboxDeReprogramacion()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (var seed = bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", fallido.Resultado);

        await using (var owner = new NpgsqlConnection(data.ConnectionString))
        {
            await owner.OpenAsync();
            await using var command = owner.CreateCommand();
            command.CommandText = """
                CREATE FUNCTION public.test_reject_reprogram_history() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."OrigenTipo" = 'ReprogramacionEnvio' THEN
                        RAISE EXCEPTION 'Injected history failure for DEC-04 rollback test';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER test_reject_reprogram_history
                    BEFORE INSERT ON public."EnvioEventos"
                    FOR EACH ROW EXECUTE FUNCTION public.test_reject_reprogram_history();
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(async () =>
        {
            using var respuesta = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion",
                new { VersionEnvio = fallido.VersionEnvio });
        });
        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.NoEntregado, (await verify.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.True((await verify.ParadasRuta.SingleAsync(x => x.EnvioId == parada.EnvioId)).Activa);
        Assert.Empty(await verify.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado && x.EstadoAnterior == "NoEntregado"
            && x.EstadoNuevo == "Reprogramado").ToArrayAsync());
        Assert.Empty(await verify.AvisosReparto.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == "Reasignacion").ToArrayAsync());
        Assert.Empty(await verify.OutboxMessages.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == OutboxMessageTypes.DeliveryChanged).ToArrayAsync());
    }

    [Fact]
    public async Task RendicionYRecepcionNoEludenElAgotamientoDeDecisionOperativa()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (var seed = bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", fallido.Resultado);
        var rendicion = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "Rendicion",
            datos: new { BultoIds = parada.Bultos.Select(b => b.Id).ToArray() }));
        Assert.Equal("Aplicada", rendicion.Resultado);
        using var recepcion = await client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion",
            new { Version = fallido.VersionEnvio });
        Assert.Equal(HttpStatusCode.NoContent, recepcion.StatusCode);
        using var reprogramacion = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion",
            new { VersionEnvio = fallido.VersionEnvio });
        Assert.Equal(HttpStatusCode.Conflict, reprogramacion.StatusCode);
        await using var db = data.CreateDbContext();
        var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        Assert.Equal(EstadoEnvio.NoEntregado, envio.Estado);
        Assert.Equal(EstadoCustodiaBulto.Deposito, (await db.Bultos.SingleAsync(x => x.EnvioId == envio.Id)).Custodia);
    }

    [Fact]
    public async Task ReprogramacionNoPermiteEludirIntervaloNiExigeCambiarReglas()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (var seed = bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", fallido.Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "FinJornada"))).Resultado);

        using var reprogramar = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion",
            new { VersionEnvio = fallido.VersionEnvio });
        Assert.Equal(HttpStatusCode.OK, reprogramar.StatusCode);
        var reprogramado = (await reprogramar.Content.ReadFromJsonAsync<ReprogramacionEnvioResponse>())!;
        Assert.Equal(fallido.VersionEnvio + 1, reprogramado.VersionEnvio);
        using var recibir = await client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion",
            new { Version = reprogramado.VersionEnvio });
        Assert.Equal(HttpStatusCode.NoContent, recibir.StatusCode);

        var plan = new
        {
            RepartidorAccesoId = hoja.RepartidorAccesoId,
            VehiculoId = hoja.VehiculoId,
            Fecha = hoja.Fecha,
            Paradas = new[] { new { parada.EnvioId, LlegadaEstimadaUtc = LlegadaEstimada.AddMinutes(1) } }
        };
        using var planResponse = await client.PostAsJsonAsync("/api/rutas", plan);
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
        var rutaNueva = (await planResponse.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("AsignadoRuta", rutaNueva.Paradas.Single().Estado);
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{rutaNueva.Id}/despacho", new { rutaNueva.Version });
        Assert.Equal(HttpStatusCode.OK, despacho.StatusCode);
        var hojaMovil = (await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={hoja.Fecha:yyyy-MM-dd}"))!
            .Single(x => x.Id == rutaNueva.Id);
        Assert.Equal("Aplicada", (await Aplicar(client, hojaMovil, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
        var paradaNueva = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{rutaNueva.Id}"))!.Paradas.Single();
        Assert.Equal("Aplicada", (await Aplicar(client, hojaMovil, Operacion(Guid.NewGuid(), 2, "CargarBulto",
            parada.EnvioId, paradaNueva.EnvioVersion, new { BultoId = paradaNueva.Bultos.Single().Id }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hojaMovil, Operacion(Guid.NewGuid(), 3, "ConfirmarCarga"))).Resultado);
        var lista = (await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={hoja.Fecha:yyyy-MM-dd}"))!
            .Single(x => x.Id == rutaNueva.Id);
        var paradaEnTransito = Assert.Single(lista.Paradas);
        Assert.Equal("EnTransito", paradaEnTransito.Estado);

        var demasiadoPronto = await Aplicar(client, lista, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "EntregaFisica", EnvioId = parada.EnvioId,
            VersionEnvio = paradaEnTransito.EnvioVersion, OcurridaEn = LlegadaEstimada.AddMinutes(29),
            Datos = new { Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10,
                PosicionEnUtc = LlegadaEstimada.AddMinutes(29) }
        });
        Assert.Equal("Conflicto", demasiadoPronto.Resultado);
        var falloDemasiadoPronto = await Aplicar(client, lista, new
        {
            Id = Guid.NewGuid(), Secuencia = 5, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = paradaEnTransito.EnvioVersion, OcurridaEn = LlegadaEstimada.AddMinutes(29),
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Conflicto", falloDemasiadoPronto.Resultado);
        var versionVigente = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{rutaNueva.Id}"))!
            .Paradas.Single().EnvioVersion;
        var limite = await Aplicar(client, lista, new
        {
            Id = Guid.NewGuid(), Secuencia = 6, Tipo = "EntregaFisica", EnvioId = parada.EnvioId,
            VersionEnvio = versionVigente, OcurridaEn = LlegadaEstimada.AddMinutes(30),
            Datos = new { Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10,
                PosicionEnUtc = LlegadaEstimada.AddMinutes(30) }
        });
        Assert.Equal("Aplicada", limite.Resultado);
        await using var db = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.Entregado,
            (await db.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.Equal(1, await db.OperacionesJornada.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada"));
    }

    [Fact]
    public async Task AgotamientoAutomaticoConcurrenteImpideReprogramarDesdeUnaVersionAntigua()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (var seed = bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 2,
            politica: PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var primerIntento = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", primerIntento.Resultado);
        using var reprogramacionInicial = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion",
            new { VersionEnvio = primerIntento.VersionEnvio });
        Assert.Equal(HttpStatusCode.OK, reprogramacionInicial.StatusCode);
        var reprogramado = (await reprogramacionInicial.Content.ReadFromJsonAsync<ReprogramacionEnvioResponse>())!;

        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "Rendicion",
            datos: new { BultoIds = parada.Bultos.Select(b => b.Id).ToArray() }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 6, "FinJornada"))).Resultado);
        using var recibir = await client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion",
            new { Version = reprogramado.VersionEnvio });
        Assert.Equal(HttpStatusCode.NoContent, recibir.StatusCode);

        var rutaNuevaResponse = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = hoja.RepartidorAccesoId,
            VehiculoId = hoja.VehiculoId,
            Fecha = hoja.Fecha,
            Paradas = new[] { new { EnvioId = parada.EnvioId, LlegadaEstimadaUtc = LlegadaEstimada.AddMinutes(30) } }
        });
        using (rutaNuevaResponse)
        {
            Assert.Equal(HttpStatusCode.OK, rutaNuevaResponse.StatusCode);
            var rutaNueva = (await rutaNuevaResponse.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
            using var despacho = await client.PostAsJsonAsync($"/api/rutas/{rutaNueva.Id}/despacho", new { rutaNueva.Version });
            Assert.Equal(HttpStatusCode.OK, despacho.StatusCode);
            var hojaNueva = (await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={hoja.Fecha:yyyy-MM-dd}"))!
                .Single(x => x.Id == rutaNueva.Id);
            Assert.Equal("Aplicada", (await Aplicar(client, hojaNueva, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
            var carga = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{rutaNueva.Id}"))!.Paradas.Single();
            Assert.Equal("Aplicada", (await Aplicar(client, hojaNueva, Operacion(Guid.NewGuid(), 2, "CargarBulto",
                parada.EnvioId, carga.EnvioVersion, new { BultoId = carga.Bultos.Single().Id }))).Resultado);
            Assert.Equal("Aplicada", (await Aplicar(client, hojaNueva, Operacion(Guid.NewGuid(), 3, "ConfirmarCarga"))).Resultado);
            var enTransito = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{rutaNueva.Id}"))!.Paradas.Single();

            var siguienteIntento = Aplicar(client, hojaNueva, new
            {
                Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
                VersionEnvio = enTransito.EnvioVersion, OcurridaEn = LlegadaEstimada.AddMinutes(30),
                Datos = new { Motivo = "AUSENTE" }
            });
            var reprogramarVersionAntigua = client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reprogramacion",
                new { VersionEnvio = primerIntento.VersionEnvio });
            await Task.WhenAll(siguienteIntento, reprogramarVersionAntigua);
            using var respuestaReprogramacion = await reprogramarVersionAntigua;
            Assert.Equal("Aplicada", (await siguienteIntento).Resultado);
            Assert.Equal(HttpStatusCode.Conflict, respuestaReprogramacion.StatusCode);
        }

        await using var verify = data.CreateDbContext();
        var envio = await verify.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        Assert.Equal(EstadoEnvio.EnDevolucion, envio.Estado);
        Assert.Equal(2, await verify.OperacionesJornada.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada"));
        var transitions = await verify.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado).ToArrayAsync();
        Assert.Contains(transitions, x => x.EstadoAnterior == "EnTransito" && x.EstadoNuevo == "NoEntregado");
        Assert.Contains(transitions, x => x.EstadoAnterior == "NoEntregado" && x.EstadoNuevo == "EnDevolucion");
        Assert.DoesNotContain(transitions, x => x.EstadoAnterior == "EnTransito" && x.EstadoNuevo == "EnDevolucion");
    }

    private static async Task PublicarReglasDeReprogramacion(IntegrationDatabase data, int maximo = 3,
        PoliticaDevolucionAlAgotarIntentos politica = PoliticaDevolucionAlAgotarIntentos.DecisionOperativa)
    {
        await using var db = data.CreateDbContext();
        var acceso = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var repository = new EfReglasOperativasRepository(db);
        await repository.CrearBorradorAsync(acceso.OperadorId!.Value, acceso.UsuarioId);
        await repository.EditarBorradorAsync(acceso.OperadorId.Value, acceso.UsuarioId, 0,
            new(maximo, 30, politica, 2880, 1440, 60,
                [TipoPruebaEntrega.NombreReceptor], [new("AUSENTE", "Ausente", true)]));
        await repository.PublicarBorradorAsync(acceso.OperadorId.Value, acceso.UsuarioId, 1);
    }

    private static async Task<Guid> AccessId(IntegrationDatabase data, bool comercio)
    {
        await using var db = data.CreateDbContext();
        return await db.AccesosUsuario.Where(x => comercio ? x.ComercioId != null : x.OperadorId != null)
            .Select(x => x.Id).SingleAsync();
    }
}
