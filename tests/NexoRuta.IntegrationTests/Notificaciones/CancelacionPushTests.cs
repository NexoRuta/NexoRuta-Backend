using System.Net;
using System.Net.Http.Json;
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
            Assert.Equal(EstadoEnvio.EntregaSinValidacion, envio.Estado); Assert.NotNull(envio.EntregaFisicaEnUtc);
            Assert.Equal(EstadoCustodiaBulto.Destinatario, (await final.Bultos.SingleAsync()).Custodia);
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
    }

    [Fact]
    public async Task CancelarReasignadoNotificaAlCustodioOriginalYNuncaAlRepartidorFuturo()
    {
        using var p = await PrepararPush(1, registrarAviso: false);
        var origen = await CargarParaCancelar(p); var parada = origen.Paradas[0];
        var destino = await CrearDestino(p, vacia: true);
        using var mover = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reasignacion", new
        {
            RutaDestinoId = destino.Id, VersionEnvio = parada.EnvioVersion, VersionRutaDestino = destino.Version,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1), Motivo = "Entrega futura."
        });
        Assert.Equal(HttpStatusCode.OK, mover.StatusCode);
        await using var db = p.Data.CreateDbContext();
        var version = (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Version;
        var versionCustodio = (await db.HojasRuta.AsNoTracking().SingleAsync(h => h.Id == origen.Id)).Version;
        using var cancelar = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/cancelacion", new { Version = version, Motivo = "Ya no se necesita." });
        Assert.Equal(HttpStatusCode.OK, cancelar.StatusCode);
        var avisos = await db.AvisosReparto.ToArrayAsync(); Assert.Equal(2, avisos.Length);
        Assert.All(avisos, aviso => Assert.Equal((origen.Id, origen.RepartidorAccesoId), (aviso.RutaId, aviso.AccesoId)));
        Assert.False((await db.ParadasRuta.SingleAsync(x => x.RutaId == destino.Id)).Activa);
        Assert.True((await db.HojasRuta.AsNoTracking().SingleAsync(h => h.Id == origen.Id)).Version > versionCustodio);
        await using var services = await ServiciosWorker(p); var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default)); Assert.True(await worker.ProcesarSiguienteAsync(default));
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
    public async Task EntregaYCancelacionConcurrentesConservanLaEntregaSinDecisionManual()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        var hoja = await CargarParaCancelar(p); var parada = hoja.Paradas[0];
        var cancelar = p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/cancelacion", new { Version = parada.EnvioVersion, Motivo = "Solicitud concurrente." });
        var entregar = Operar(p.Client, hoja, 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion);
        using var cancelacion = await cancelar;
        Assert.Contains(cancelacion.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Equal("Aplicada", (await entregar).Resultado);
        await using var db = p.Data.CreateDbContext(); var envio = await db.Envios.SingleAsync();
        Assert.NotNull(envio.EntregaFisicaEnUtc); Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, envio.Estado);
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
