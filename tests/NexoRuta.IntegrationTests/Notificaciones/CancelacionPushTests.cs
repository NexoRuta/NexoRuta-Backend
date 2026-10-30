using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Envios;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Worker;
using Npgsql;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Notificaciones;

public sealed partial class PushOutboxTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancelacionAutenticadaEsAtomicaYNoDeshaceUnaEntregaRecibida(bool comercio, bool recibio)
    {
        using var p = await PrepararPush(1, registrarAviso: false);
        var origen = await CargarParaCancelar(p); var parada = origen.Paradas[0];
        var auth = p.Client.DefaultRequestHeaders.Authorization;
        if (comercio)
        {
            await using var db = p.Data.CreateDbContext();
            await p.Client.SetBearerForAccessAsync(p.Data, (await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null)).Id);
        }
        var body = new { Version = parada.EnvioVersion, Motivo = "El cliente solicitó cancelar." };
        var url = $"/api/envios/{parada.EnvioId}/cancelacion";
        var actorCancelacion = p.Client.DefaultRequestHeaders.Authorization;
        using var respuesta = await p.Client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<EnvioCancelado>())!;
        Assert.Equal("Cancelado", resultado.Estado); Assert.Equal(parada.EnvioVersion + 1, resultado.Version);
        using var repetir = await p.Client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, repetir.StatusCode);
        Assert.Equal(resultado, await repetir.Content.ReadFromJsonAsync<EnvioCancelado>());
        await using (var db = p.Data.CreateDbContext())
        {
            var audit = Assert.Single(await db.CancelacionesEnvio.ToArrayAsync());
            var history = Assert.Single(await db.EnvioEventos.Where(x => x.CancelacionEnvioId == audit.Id).ToArrayAsync());
            Assert.Equal((TipoEventoEnvio.TransicionEstado, "EnTransito", "Cancelado", parada.EnvioVersion,
                parada.EnvioVersion + 1, audit.Id), (history.Tipo, history.EstadoAnterior, history.EstadoNuevo,
                history.VersionAnterior, history.VersionNueva, history.CancelacionEnvioId));
            Assert.Equal(comercio ? TipoAccesoUsuario.Comercio : TipoAccesoUsuario.Operador, audit.TipoActor);
            Assert.Equal("EnTransito", audit.EstadoAnterior);
            Assert.Equal(EstadoCustodiaBulto.Repartidor, (await db.Bultos.SingleAsync()).Custodia);
            Assert.False((await db.ParadasRuta.SingleAsync()).Activa);
            var aviso = Assert.Single(await db.AvisosReparto.ToArrayAsync());
            Assert.Equal((origen.Id, origen.RepartidorAccesoId, "Cancelacion"), (aviso.RutaId, aviso.AccesoId, aviso.Tipo));
            Assert.Single(await db.OutboxMessages.Where(x => x.Destino == OutboxDestino.PushFcm).ToArrayAsync());
            var inmutable = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"CancelacionesEnvio\" SET \"Motivo\"='otro'"));
            Assert.Equal("55000", inmutable.SqlState);
        }
        p.Client.DefaultRequestHeaders.Authorization = auth;
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Equal(p.Fids, Assert.Single(transporte.Enviados).Fids);
        var operacion = recibio
            ? await Operar(p.Client, origen, 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion)
            : await Operar(p.Client, origen, 4, "Rendicion", datos: new { BultoIds = new[] { parada.Bultos[0].Id } });
        Assert.Equal("Aplicada", operacion.Resultado);
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 5, "FinJornada")).Resultado);
        await using var final = p.Data.CreateDbContext();
        var envio = await final.Envios.SingleAsync(e => e.Id == parada.EnvioId);
        if (recibio)
        {
            Assert.Equal(EstadoEnvio.Cancelado, envio.Estado); Assert.NotNull(envio.EntregaFisicaEnUtc);
            Assert.Equal(EstadoCustodiaBulto.Destinatario, (await final.Bultos.SingleAsync()).Custodia);
            p.Client.DefaultRequestHeaders.Authorization = actorCancelacion;
            using var repetirDespuesDeHechoFisico = await p.Client.PostAsJsonAsync(url, body);
            Assert.Equal(HttpStatusCode.OK, repetirDespuesDeHechoFisico.StatusCode);
            Assert.Equal(resultado, await repetirDespuesDeHechoFisico.Content.ReadFromJsonAsync<EnvioCancelado>());
            p.Client.DefaultRequestHeaders.Authorization = auth;
            using var retroactiva = await p.Client.PostAsJsonAsync(url, new { envio.Version, Motivo = "Intento posterior a entrega." });
            Assert.Equal(HttpStatusCode.Conflict, retroactiva.StatusCode);
        }
        else
        {
            Assert.Equal(EstadoEnvio.Cancelado, envio.Estado); Assert.Null(envio.EntregaFisicaEnUtc);
            using var recepcion = await p.Client.PostAsJsonAsync($"/api/deposito/envios/{envio.Id}/recepcion", new { envio.Version });
            Assert.Equal(HttpStatusCode.NoContent, recepcion.StatusCode);
        }
        Assert.Single(await final.CancelacionesEnvio.ToArrayAsync());
        Assert.Single(await final.OutboxMessages.Where(x => x.Destino == OutboxDestino.PushFcm).ToArrayAsync());
        Assert.Single(await final.EnvioEventos.Where(x => x.CancelacionEnvioId != null).ToArrayAsync());
    }

    [Theory]
    [InlineData(EstadoEnvio.Admitido, true)]
    [InlineData(EstadoEnvio.EnDeposito, true)]
    [InlineData(EstadoEnvio.AsignadoRuta, true)]
    [InlineData(EstadoEnvio.EnTransito, true)]
    [InlineData(EstadoEnvio.NoEntregado, true)]
    [InlineData(EstadoEnvio.Reprogramado, true)]
    [InlineData(EstadoEnvio.EnDevolucion, true)]
    [InlineData(EstadoEnvio.Entregado, false)]
    [InlineData(EstadoEnvio.Devuelto, false)]
    [InlineData(EstadoEnvio.Extraviado, false)]
    [InlineData(EstadoEnvio.EntregaPendienteValidacion, false)]
    [InlineData(EstadoEnvio.EntregaSinValidacion, false)]
    public async Task CancelacionAutenticadaRespetaLaMatrizDeEstados(EstadoEnvio estado, bool permitida)
    {
        var sinRuta = estado is EstadoEnvio.Admitido or EstadoEnvio.EnDeposito;
        var agotaAutomaticamente = estado is EstadoEnvio.EnDevolucion or EstadoEnvio.Devuelto;
        using var p = await PrepararPush(1, registrarAviso: false, recibir: estado != EstadoEnvio.Admitido,
            planificar: !sinRuta, automatizarDevolucion: agotaAutomaticamente);
        var ruta = await AlcanzarEstadoMatrizCancelacionAsync(p, estado);
        var rutaId = ruta is null ? (Guid?)null : p.Aviso.RutaId;
        var envioId = p.Aviso.EnvioId;
        var fixture = await CapturarEstadoMatrizCancelacionAsync(p.Data, envioId, rutaId, estado);

        using var response = await p.Client.PostAsJsonAsync($"/api/envios/{envioId}/cancelacion",
            new { Version = fixture.Version, Motivo = "Cancelación de prueba." });
        Assert.Equal(permitida ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);

        await using var result = p.Data.CreateDbContext();
        var envio = await result.Envios.SingleAsync(e => e.Id == envioId);
        Assert.Equal(permitida ? EstadoEnvio.Cancelado : estado, envio.Estado);
        Assert.Equal(fixture.Version + (permitida ? 1 : 0), envio.Version);
        Assert.Equal(fixture.EntregaFisicaEnUtc, envio.EntregaFisicaEnUtc);
        Assert.Equal(fixture.EntregaFisicaRecibidaEnUtc, envio.EntregaFisicaRecibidaEnUtc);
        Assert.Equal(fixture.PlazoPruebaVenceEnUtc, envio.PlazoPruebaVenceEnUtc);
        Assert.Equal(fixture.Custodia, (await result.Bultos.SingleAsync(b => b.EnvioId == envio.Id)).Custodia);
        Assert.Equal(fixture.VersionBulto, (await result.Bultos.SingleAsync(b => b.EnvioId == envio.Id)).Version);

        var auditCount = await result.CancelacionesEnvio.CountAsync(c => c.EnvioId == envio.Id);
        var noticeCount = await result.AvisosReparto.CountAsync(a => a.EnvioId == envio.Id);
        var pushCount = await result.OutboxMessages.CountAsync(m => m.EnvioId == envio.Id && m.Destino == OutboxDestino.PushFcm);
        var allOutboxCount = await result.OutboxMessages.CountAsync(m => m.EnvioId == envio.Id);
        var history = await result.EnvioEventos.Where(x => x.EnvioId == envio.Id).ToArrayAsync();
        Assert.Equal(fixture.Cancelaciones + (permitida ? 1 : 0), auditCount);
        var expectedNotice = permitida && fixture.ShouldNotifyRoute ? 1 : 0;
        Assert.Equal(fixture.Avisos + expectedNotice, noticeCount);
        Assert.Equal(fixture.PushMessages + expectedNotice, pushCount);
        Assert.Equal(fixture.AllOutboxMessages + expectedNotice, allOutboxCount);
        Assert.Equal(fixture.HistoryIds.Length + (permitida ? 1 : 0), history.Length);
        Assert.Equal(fixture.HistoryIds.Order(), history.Select(x => x.Id).Where(fixture.HistoryIds.Contains).Order());
        if (rutaId is { } id)
        {
            Assert.Equal(fixture.VersionRuta!.Value + expectedNotice,
                (await result.HojasRuta.SingleAsync(x => x.Id == id)).Version);
            Assert.Equal(permitida && fixture.ShouldNotifyRoute ? false : fixture.ParadaActiva!.Value,
                (await result.ParadasRuta.SingleAsync(x => x.EnvioId == envio.Id)).Activa);
        }
        else
        {
            Assert.Empty(await result.ParadasRuta.Where(x => x.EnvioId == envio.Id).ToArrayAsync());
        }

        if (permitida)
        {
            var transition = Assert.Single(history, x => !fixture.HistoryIds.Contains(x.Id));
            Assert.Equal((TipoEventoEnvio.TransicionEstado, estado.ToString(), EstadoEnvio.Cancelado.ToString(),
                    fixture.Version, fixture.Version + 1, "CancelacionEnvio"),
                (transition.Tipo, transition.EstadoAnterior, transition.EstadoNuevo, transition.VersionAnterior,
                    transition.VersionNueva, transition.OrigenTipo));
            Assert.Single(await result.CancelacionesEnvio.Where(c => c.EnvioId == envio.Id).ToArrayAsync());
        }
        else
        {
            Assert.DoesNotContain(history, x => !fixture.HistoryIds.Contains(x.Id));
        }
    }

    private async Task<HojaRutaResponse?> AlcanzarEstadoMatrizCancelacionAsync(PreparadoPush prepared, EstadoEnvio target)
    {
        if (target is EstadoEnvio.Admitido or EstadoEnvio.EnDeposito) return null;
        var route = (await prepared.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{prepared.Aviso.RutaId}"))!;
        if (target == EstadoEnvio.AsignadoRuta) return route;
        route = await CargarParaCancelar(prepared);
        var stop = Assert.Single(route.Paradas);
        if (target == EstadoEnvio.EnTransito) return route;

        if (target == EstadoEnvio.Extraviado)
        {
            var version = await CurrentShipmentVersion(prepared.Data, stop.EnvioId);
            using var loss = await prepared.Client.PostAsJsonAsync($"/api/envios/{stop.EnvioId}/confirmar-extravio",
                new { VersionEnvio = version, ComandoId = Guid.CreateVersion7(), Motivo = "Extravío confirmado para fixture de matriz." });
            Assert.Equal(HttpStatusCode.OK, loss.StatusCode);
            return route;
        }

        if (target is EstadoEnvio.Entregado or EstadoEnvio.EntregaPendienteValidacion or EstadoEnvio.EntregaSinValidacion)
        {
            var delivery = await Operar(prepared.Client, route, 4, "EntregaFisica", stop.EnvioId, stop.EnvioVersion);
            Assert.Equal("Aplicada", delivery.Resultado);
            if (target == EstadoEnvio.EntregaPendienteValidacion) return route;
            if (target == EstadoEnvio.Entregado)
            {
                var photoId = Guid.CreateVersion7();
                using var upload = await prepared.Client.PutAsJsonAsync($"/api/mobile/jornadas/{route.JornadaId}/evidencias/{photoId}",
                    new { EnvioId = stop.EnvioId, Tipo = "Foto", Contenido = MatrixEvidenceBytes });
                Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
                var proof = await Operar(prepared.Client, route, 5, "EvidenciaEntrega", stop.EnvioId, delivery.VersionEnvio,
                    new { FotoId = photoId, Nombre = "Destinatario de prueba", Latitud = -34.9, Longitud = -56.2,
                        PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow });
                Assert.Equal("Aplicada", proof.Resultado);
                Assert.Equal(EstadoEnvio.Entregado.ToString(), proof.EstadosEnvio[stop.EnvioId]);
                return route;
            }
            var deadline = await ReadProofDeadline(prepared.Data, stop.EnvioId);
            Assert.Equal(1, await ExpireProofDeadlineAsync(prepared.Data, deadline));
            return route;
        }

        var failed = await Operar(prepared.Client, route, 4, "IntentoFallido", stop.EnvioId,
            stop.EnvioVersion, new { Motivo = "AUSENTE" });
        Assert.Equal("Aplicada", failed.Resultado);
        if (target == EstadoEnvio.NoEntregado) return route;
        if (target == EstadoEnvio.Reprogramado)
        {
            using var reschedule = await prepared.Client.PostAsJsonAsync($"/api/envios/{stop.EnvioId}/reprogramacion",
                new { VersionEnvio = failed.VersionEnvio });
            Assert.Equal(HttpStatusCode.OK, reschedule.StatusCode);
            return route;
        }
        if (target is EstadoEnvio.EnDevolucion or EstadoEnvio.Devuelto)
        {
            Assert.Equal(EstadoEnvio.EnDevolucion.ToString(), failed.EstadosEnvio[stop.EnvioId]);
            if (target == EstadoEnvio.Devuelto)
            {
                var bultoId = Assert.Single(stop.Bultos).Id;
                Assert.Equal("Aplicada", (await Operar(prepared.Client, route, 5, "Rendicion",
                    datos: new { BultoIds = new[] { bultoId } })).Resultado);
                var version = await CurrentShipmentVersion(prepared.Data, stop.EnvioId);
                using var receipt = await prepared.Client.PostAsJsonAsync($"/api/deposito/envios/{stop.EnvioId}/recepcion",
                    new { Version = version });
                Assert.Equal(HttpStatusCode.NoContent, receipt.StatusCode);
            }
            return route;
        }
        throw new ArgumentOutOfRangeException(nameof(target), target, "No configured business flow reaches the requested cancellation state.");
    }

    private static async Task<EstadoMatrizCancelacion> CapturarEstadoMatrizCancelacionAsync(
        IntegrationDatabase data, Guid envioId, Guid? routeId, EstadoEnvio state)
    {
        await using var db = data.CreateDbContext();
        var shipment = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == envioId);
        var parcel = await db.Bultos.AsNoTracking().SingleAsync(b => b.EnvioId == envioId);
        var historyIds = await db.EnvioEventos.Where(h => h.EnvioId == envioId).Select(h => h.Id).ToArrayAsync();
        var activeStop = routeId is { } id
            ? await db.ParadasRuta.AsNoTracking().Where(p => p.EnvioId == envioId && p.RutaId == id)
                .Select(p => (bool?)p.Activa).SingleOrDefaultAsync()
            : null;
        var routeVersion = routeId is { } route
            ? await db.HojasRuta.AsNoTracking().Where(r => r.Id == route).Select(r => (long?)r.Version).SingleAsync()
            : null;
        return new EstadoMatrizCancelacion(shipment.Version, shipment.EntregaFisicaEnUtc,
            shipment.EntregaFisicaRecibidaEnUtc, shipment.PlazoPruebaVenceEnUtc, parcel.Custodia,
            parcel.Version, historyIds, await db.CancelacionesEnvio.CountAsync(c => c.EnvioId == envioId),
            await db.AvisosReparto.CountAsync(a => a.EnvioId == envioId),
            await db.OutboxMessages.CountAsync(m => m.EnvioId == envioId && m.Destino == OutboxDestino.PushFcm),
            await db.OutboxMessages.CountAsync(m => m.EnvioId == envioId), activeStop, routeVersion,
            routeId is not null && state is EstadoEnvio.AsignadoRuta or EstadoEnvio.EnTransito or EstadoEnvio.NoEntregado
                or EstadoEnvio.Reprogramado or EstadoEnvio.EnDevolucion);
    }

    private static async Task<long> CurrentShipmentVersion(IntegrationDatabase data, Guid shipmentId)
    {
        await using var db = data.CreateDbContext();
        return await db.Envios.AsNoTracking().Where(x => x.Id == shipmentId).Select(x => x.Version).SingleAsync();
    }

    private static async Task<DateTimeOffset> ReadProofDeadline(IntegrationDatabase data, Guid shipmentId)
    {
        await using var db = data.CreateDbContext();
        return (await db.Envios.AsNoTracking().Where(x => x.Id == shipmentId)
            .Select(x => x.PlazoPruebaVenceEnUtc).SingleAsync())!.Value;
    }

    private static async Task<int> ExpireProofDeadlineAsync(IntegrationDatabase data, DateTimeOffset now)
    {
        var role = $"matrix_worker_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var owner = new NpgsqlConnection(data.ConnectionString))
        {
            await owner.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; GRANT nexoruta_worker TO {role};", owner);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(data.ConnectionString)
        { Username = role, Password = password, Pooling = false }.ConnectionString;
        await using var worker = new NpgsqlConnection(connectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT nexoruta_app.expire_pending_proof_deadlines(@now, 100)", worker);
        command.Parameters.AddWithValue("now", now);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task SetLocalHistoryFixtureSettingAsync(NexoRutaDbContext db, string name, string value) =>
        _ = await db.Database.SqlQuery<string>($"SELECT set_config({name}, {value}, true) AS \"Value\"").SingleAsync();

    private static readonly byte[] MatrixEvidenceBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");

    private sealed record EstadoMatrizCancelacion(long Version, DateTimeOffset? EntregaFisicaEnUtc,
        DateTimeOffset? EntregaFisicaRecibidaEnUtc, DateTimeOffset? PlazoPruebaVenceEnUtc,
        EstadoCustodiaBulto Custodia, long VersionBulto, Guid[] HistoryIds, int Cancelaciones, int Avisos,
        int PushMessages, int AllOutboxMessages, bool? ParadaActiva, long? VersionRuta, bool ShouldNotifyRoute);
    [Fact]
    public async Task ReasignacionEnTransitoSeRechazaYCancelacionNotificaAlCustodioActual()
    {
        using var p = await PrepararPush(1, registrarAviso: false);
        var origen = await CargarParaCancelar(p); var parada = origen.Paradas[0];
        var destino = await CrearDestino(p, vacia: true);
        long version; long routeVersion; EstadoCustodiaBulto custody; int historyCount;
        await using (var before = p.Data.CreateDbContext())
        {
            var shipment = await before.Envios.SingleAsync(x => x.Id == parada.EnvioId);
            Assert.Equal(EstadoEnvio.EnTransito, shipment.Estado);
            version = shipment.Version;
            routeVersion = (await before.HojasRuta.SingleAsync(x => x.Id == origen.Id)).Version;
            custody = (await before.Bultos.SingleAsync(x => x.EnvioId == parada.EnvioId)).Custodia;
            historyCount = await before.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId);
        }
        using var mover = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reasignacion", new
        {
            RutaDestinoId = destino.Id, VersionEnvio = version, VersionRutaDestino = destino.Version,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1), Motivo = "Entrega futura."
        });
        Assert.Equal(HttpStatusCode.Conflict, mover.StatusCode);
        Assert.Contains("En tránsito", await mover.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using (var afterRejectedMove = p.Data.CreateDbContext())
        {
            var shipment = await afterRejectedMove.Envios.SingleAsync(x => x.Id == parada.EnvioId);
            Assert.Equal((EstadoEnvio.EnTransito, version), (shipment.Estado, shipment.Version));
            Assert.Equal(custody, (await afterRejectedMove.Bultos.SingleAsync(x => x.EnvioId == parada.EnvioId)).Custodia);
            Assert.Equal(routeVersion, (await afterRejectedMove.HojasRuta.SingleAsync(x => x.Id == origen.Id)).Version);
            Assert.True((await afterRejectedMove.ParadasRuta.SingleAsync(x => x.RutaId == origen.Id && x.EnvioId == parada.EnvioId)).Activa);
            Assert.Empty(await afterRejectedMove.ParadasRuta.Where(x => x.RutaId == destino.Id && x.EnvioId == parada.EnvioId).ToArrayAsync());
            Assert.Equal(historyCount, await afterRejectedMove.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId));
            Assert.Empty(await afterRejectedMove.AvisosReparto.ToArrayAsync());
            Assert.False(await afterRejectedMove.OutboxMessages.AnyAsync(x => x.Destino == OutboxDestino.PushFcm));
        }
        using var cancelar = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/cancelacion", new { Version = version, Motivo = "Ya no se necesita." });
        Assert.Equal(HttpStatusCode.OK, cancelar.StatusCode);
        await using var db = p.Data.CreateDbContext();
        var avisos = await db.AvisosReparto.ToArrayAsync(); Assert.Single(avisos);
        Assert.All(avisos, aviso => Assert.Equal((origen.Id, origen.RepartidorAccesoId), (aviso.RutaId, aviso.AccesoId)));
        Assert.Equal(EstadoEnvio.Cancelado, (await db.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.False((await db.ParadasRuta.SingleAsync(x => x.RutaId == origen.Id && x.EnvioId == parada.EnvioId)).Activa);
        Assert.Empty(await db.ParadasRuta.Where(x => x.RutaId == destino.Id && x.EnvioId == parada.EnvioId).ToArrayAsync());
        Assert.Single(await db.CancelacionesEnvio.Where(x => x.EnvioId == parada.EnvioId).ToArrayAsync());
        Assert.Single(await db.OutboxMessages.Where(x => x.EnvioId == parada.EnvioId && x.Destino == OutboxDestino.PushFcm).ToArrayAsync());
        await using var services = await ServiciosWorker(p); var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Equal(p.Fids, Assert.Single(transporte.Enviados).Fids);
    }

    [Fact]
    public async Task VersionObsoletaNoCancelaNiSuspendeNiEncolaAvisos()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        var origen = await CargarParaCancelar(p); var parada = origen.Paradas[0];
        using var respuesta = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/cancelacion", new { Version = parada.EnvioVersion - 1, Motivo = "Fuera de fecha." });
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        await using var db = p.Data.CreateDbContext();
        Assert.Equal(EstadoEnvio.EnTransito, (await db.Envios.SingleAsync()).Estado);
        Assert.True((await db.ParadasRuta.SingleAsync()).Activa); Assert.Empty(await db.CancelacionesEnvio.ToArrayAsync());
        Assert.Empty(await db.AvisosReparto.ToArrayAsync()); Assert.False(await db.OutboxMessages.AnyAsync(m => m.Destino == OutboxDestino.PushFcm));
    }

    [Fact]
    public async Task DespachadorConGestionDeEnviosNoPuedeCancelar()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        await using (var db = p.Data.CreateDbContext())
        {
            var acceso = await db.AccesosUsuario.SingleAsync(a => a.Id == p.Aviso.AccesoId);
            var rol = await db.Roles.SingleAsync(r => r.OperadorId == acceso.OperadorId && r.Codigo == "usuario_operador");
            acceso.AsignarRol(rol); await db.SaveChangesAsync();
        }
        await p.Client.SetBearerForAccessAsync(p.Data, p.Aviso.AccesoId);
        using var respuesta = await p.Client.PostAsJsonAsync($"/api/envios/{p.Aviso.EnvioId}/cancelacion", new { Version = p.Aviso.VersionEnvio, Motivo = "No autorizado." });
        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        await using var final = p.Data.CreateDbContext(); Assert.Empty(await final.CancelacionesEnvio.ToArrayAsync());
    }

    [Fact]
    public async Task PersistenciaImpideReactivarUnEnvioCancelado()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        await using (var db = p.Data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == p.Aviso.EnvioId);
            envio.CambiarEstado(EstadoEnvio.Cancelado, envio.Version);
            await db.SaveChangesAsync();
        }

        await using (var db = p.Data.CreateDbContext())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Envios.Where(e => e.Id == p.Aviso.EnvioId)
                .ExecuteUpdateAsync(update => update.SetProperty(e => e.Estado, EstadoEnvio.EnTransito)));
            Assert.Equal("23514", error.SqlState);
            Assert.Equal("CK_Envios_TransicionEstado", error.ConstraintName);
        }
        await using var final = p.Data.CreateDbContext();
        Assert.Equal(EstadoEnvio.Cancelado, (await final.Envios.SingleAsync(e => e.Id == p.Aviso.EnvioId)).Estado);
    }

    [Fact]
    public async Task FuncionDeMatrizConservaEjecucionSoloParaLosRolesPrivados()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        await using var db = p.Data.CreateDbContext();
        Assert.False(await db.Database.SqlQuery<bool>($"SELECT has_function_privilege('nexoruta_api','nexoruta_app.cancel_state_is_allowed(text)','EXECUTE') AS \"Value\"").SingleAsync());
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT has_function_privilege('nexoruta_cancel_fn','nexoruta_app.cancel_state_is_allowed(text)','EXECUTE') AS \"Value\"").SingleAsync());
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT has_function_privilege('nexoruta_cancel_public_fn','nexoruta_app.cancel_state_is_allowed(text)','EXECUTE') AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task FalloAlInsertarOutboxRevierteEstadoHistorialAvisoYParada()
    {
        using var p = await PrepararPush(1, registrarAviso: false);
        var hoja = await CargarParaCancelar(p); var parada = hoja.Paradas[0];
        await using (var admin = p.Data.CreateDbContext())
            await admin.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION public.fail_test_cancellation_outbox() RETURNS trigger
                LANGUAGE plpgsql SET search_path=pg_catalog AS $$
                BEGIN
                    RAISE EXCEPTION 'forced outbox failure' USING ERRCODE='P0001';
                END $$;
                CREATE TRIGGER fail_test_cancellation_outbox BEFORE INSERT ON public."OutboxMessages"
                    FOR EACH ROW WHEN (NEW."Tipo"='RepartoModificado' AND NEW."Destino"='PushFcm')
                    EXECUTE FUNCTION public.fail_test_cancellation_outbox();
                """);

        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(p.Data);
        await using (var db = runtime.CreateDbContext())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await using var owner = p.Data.CreateDbContext();
            var acceso = await owner.AccesosUsuario.SingleAsync(a => a.ComercioId != null);
            await db.ApplyAsync(new RlsActorContext(RlsActorKind.Comercio, acceso.UsuarioId, acceso.Id, acceso.ComercioId!.Value));
            var cancelacionId = Guid.CreateVersion7();
            var avisoId = Guid.CreateVersion7();
            var mensajeId = Guid.CreateVersion7();
            await SetLocalHistoryFixtureSettingAsync(db, "nexoruta.history_source_type", "CancelacionEnvio");
            await SetLocalHistoryFixtureSettingAsync(db, "nexoruta.history_source_id", cancelacionId.ToString("D"));
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.SqlQuery<Guid>(
                $"SELECT nexoruta_app.cancel_shipment({parada.EnvioId},{parada.EnvioVersion},{"Forzar rollback"},{cancelacionId},{avisoId},{mensajeId}) AS \"Value\"").SingleAsync());
            Assert.Equal("P0001", error.SqlState);
            await tx.RollbackAsync();
        }

        await using var final = p.Data.CreateDbContext();
        var envio = await final.Envios.SingleAsync(e => e.Id == parada.EnvioId);
        Assert.Equal(EstadoEnvio.EnTransito, envio.Estado);
        Assert.Equal(parada.EnvioVersion, envio.Version);
        Assert.Empty(await final.CancelacionesEnvio.ToArrayAsync());
        Assert.Empty(await final.AvisosReparto.ToArrayAsync());
        Assert.False(await final.OutboxMessages.AnyAsync(m => m.Destino == OutboxDestino.PushFcm));
        Assert.True((await final.ParadasRuta.SingleAsync(p => p.EnvioId == parada.EnvioId)).Activa);
    }

    private static async Task<HojaRutaResponse> CargarParaCancelar(PreparadoPush p)
    {
        var hoja = (await p.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{p.Aviso.RutaId}"))!;
        var parada = hoja.Paradas[0];
        Assert.Equal("Aplicada", (await Operar(p.Client, hoja, 1, "InicioJornada")).Resultado);
        Assert.Equal("Aplicada", (await Operar(p.Client, hoja, 2, "CargarBulto", parada.EnvioId, parada.EnvioVersion, new { BultoId = parada.Bultos[0].Id })).Resultado);
        Assert.Equal("Aplicada", (await Operar(p.Client, hoja, 3, "ConfirmarCarga")).Resultado);
        return (await p.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
    }

    [Fact]
    public async Task ComercioAjenoNoPuedeCancelarPorHttpNiPorLaFuncionSql()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        using var original = await p.Client.PostAsJsonAsync($"/api/envios/{p.Aviso.EnvioId}/cancelacion",
            new { Version = p.Aviso.VersionEnvio, Motivo = "Cancelación de la cuenta autorizada." });
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var comercio = new Comercio("Otro comercio");
        var email = "otro-" + Guid.NewGuid().ToString("N") + "@example.test";
        var usuario = new Usuario(email, AuthenticationTestData.Hash(email));
        var rol = new Rol("ventas", "Ventas", null, comercio.Id);
        var acceso = new AccesoUsuario(usuario.Id, null, comercio.Id, rol);
        await using (var db = p.Data.CreateDbContext())
        {
            db.AddRange(comercio, usuario, rol); await db.SaveChangesAsync(); db.AccesosUsuario.Add(acceso);
            foreach (var permiso in await db.Permisos.Where(x => x.Codigo == "envios.crear" || x.Codigo == "envios.consultar").ToArrayAsync())
                db.RolesPermisos.Add(new RolPermiso(rol.Id, permiso.Id));
            await db.SaveChangesAsync();
        }
        await p.Client.SetBearerForAccessAsync(p.Data, acceso.Id);
        using var ajena = await p.Client.PostAsJsonAsync($"/api/envios/{p.Aviso.EnvioId}/cancelacion", new { Version = p.Aviso.VersionEnvio, Motivo = "No es de este comercio." });
        Assert.Equal(HttpStatusCode.NotFound, ajena.StatusCode);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(p.Data);
        await using var sql = runtime.CreateDbContext(); await using var tx = await sql.Database.BeginTransactionAsync();
        await sql.ApplyAsync(new RlsActorContext(RlsActorKind.Comercio, usuario.Id, acceso.Id, comercio.Id));
        Assert.Empty(await sql.Envios.ToArrayAsync()); Assert.Empty(await sql.HojasRuta.ToArrayAsync());
        Assert.Empty(await sql.CancelacionesEnvio.ToArrayAsync());
        Assert.Equal(0, await sql.Envios.Where(x => x.Id == p.Aviso.EnvioId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Estado, EstadoEnvio.Cancelado)));
        var error = await Assert.ThrowsAsync<PostgresException>(() => sql.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT nexoruta_app.cancel_shipment({p.Aviso.EnvioId},{p.Aviso.VersionEnvio},{"Prueba ajena"},{Guid.CreateVersion7()},{Guid.CreateVersion7()},{Guid.CreateVersion7()})"));
        Assert.Equal("42501", error.SqlState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelacionAntesDePlanificarNoInventaAvisoNiMovimientoFisico(bool admitido)
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        Guid envioId; long version; EstadoCustodiaBulto custodia;
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == preparado.EnvioId);
            if (admitido)
            {
                envio = new Envio(envio.OperadorId, envio.ComercioId, envio.CreadoPorUsuarioId, envio.DestinatarioId, envio.DireccionId, envio.VersionReglasId);
                db.Envios.Add(envio); db.Bultos.Add(new Bulto(envio.OperadorId, envio.Id, "NUEVO-" + Guid.NewGuid().ToString("N"), 100, 1, 1, 1));
                await db.SaveChangesAsync();
            }
            envioId = envio.Id; version = envio.Version;
            custodia = (await db.Bultos.SingleAsync(b => b.EnvioId == envioId)).Custodia;
            await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null)).Id);
        }
        using var respuesta = await client.PostAsJsonAsync($"/api/envios/{envioId}/cancelacion", new { Version = version, Motivo = "Cancelar antes de preparar reparto." });
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        await using var final = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.Cancelado, (await final.Envios.SingleAsync(e => e.Id == envioId)).Estado);
        Assert.Equal(custodia, (await final.Bultos.SingleAsync(b => b.EnvioId == envioId)).Custodia);
        Assert.Empty(await final.HojasRuta.ToArrayAsync()); Assert.Empty(await final.AvisosReparto.ToArrayAsync());
        Assert.Single(await final.CancelacionesEnvio.ToArrayAsync());
        Assert.False(await final.OutboxMessages.AnyAsync(m => m.Destino == OutboxDestino.PushFcm));
    }

    [Fact]
    public async Task DosCancelacionesConcurrentesPersistenUnSoloHechoYAviso()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        var body = new { Version = p.Aviso.VersionEnvio, Motivo = "Solicitud repetida por pérdida de conexión." };
        var respuestas = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => p.Client.PostAsJsonAsync($"/api/envios/{p.Aviso.EnvioId}/cancelacion", body)));
        try
        {
            Assert.All(respuestas, respuesta => Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode));
            Assert.Equal(await respuestas[0].Content.ReadFromJsonAsync<EnvioCancelado>(), await respuestas[1].Content.ReadFromJsonAsync<EnvioCancelado>());
        }
        finally { foreach (var respuesta in respuestas) respuesta.Dispose(); }
        await using var db = p.Data.CreateDbContext(); Assert.Single(await db.CancelacionesEnvio.ToArrayAsync());
        Assert.Single(await db.AvisosReparto.ToArrayAsync());
        Assert.Single(await db.OutboxMessages.Where(m => m.Destino == OutboxDestino.PushFcm).ToArrayAsync());
    }

    [Fact]
    public async Task EntregaYCancelacionConcurrentesRespetanElPrimerResultadoConfirmado()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        var hoja = await CargarParaCancelar(p); var parada = hoja.Paradas[0];
        var cancelar = p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/cancelacion", new { Version = parada.EnvioVersion, Motivo = "Solicitud concurrente." });
        var entregar = Operar(p.Client, hoja, 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion);
        using var cancelacion = await cancelar;
        Assert.Contains(cancelacion.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Equal("Aplicada", (await entregar).Resultado);
        await using var db = p.Data.CreateDbContext(); var envio = await db.Envios.SingleAsync();
        Assert.NotNull(envio.EntregaFisicaEnUtc);
        Assert.Equal(cancelacion.IsSuccessStatusCode ? EstadoEnvio.Cancelado : EstadoEnvio.EntregaPendienteValidacion, envio.Estado);
        Assert.Equal(EstadoCustodiaBulto.Destinatario, (await db.Bultos.SingleAsync()).Custodia);
        Assert.Equal(cancelacion.IsSuccessStatusCode ? 1 : 0, await db.CancelacionesEnvio.CountAsync());
    }

    [Fact]
    public async Task MigracionDeCancelacionRevierteSusCapacidadesSinCambiarDatosPrevios()
    {
        var data = await postgres.CreateDatabaseAsync(); await using var db = data.CreateDbContext();
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261019185305_IntegratePushOutboxDispatch");
        var funcion = await db.Database.SqlQuery<bool>($"SELECT to_regprocedure('nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid)') IS NULL AS \"Value\"").SingleAsync();
        Assert.True(funcion);
        Assert.False(await db.Database.SqlQuery<bool>($"SELECT has_column_privilege('nexoruta_cancel_fn','public.\"Envios\"','Estado','UPDATE') AS \"Value\"").SingleAsync());
        await migrator.MigrateAsync();
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT to_regprocedure('nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid)') IS NOT NULL AS \"Value\"").SingleAsync());
    }
}
