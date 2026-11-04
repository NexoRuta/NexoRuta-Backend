using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Envios;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task DecisionOperativaAgotadaIniciaDevolucionUnaSolaVezSinCambiarCustodia()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var intento = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada,
            Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", intento.Resultado);

        var comando = new { VersionEnvio = intento.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "  Devolución solicitada por gestión  " };
        var transicionesAntes = 1;
        var outboxAntes = 0;
        await using (var before = data.CreateDbContext())
        {
            transicionesAntes = await before.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
                && x.Tipo == TipoEventoEnvio.TransicionEstado);
            outboxAntes = await before.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId);
        }

        using (var missingVersion = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion",
            new { ComandoId = Guid.NewGuid(), Motivo = "Missing expected version" }))
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, missingVersion.StatusCode);
        await using (var verifyMissingVersion = data.CreateDbContext())
        {
            Assert.Equal(EstadoEnvio.NoEntregado,
                (await verifyMissingVersion.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
            Assert.Empty(await verifyMissingVersion.DecisionesDevolucionEnvio.Where(x => x.EnvioId == parada.EnvioId).ToArrayAsync());
        }

        using var response = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", comando);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal("EnDevolucion", body.GetProperty("estadoDecision").GetString());
        Assert.Equal("EnDevolucion", body.GetProperty("estadoActual").GetString());
        Assert.Equal("Devolución solicitada por gestión", body.GetProperty("motivo").GetString());
        Assert.False(body.GetProperty("repetida").GetBoolean());

        await using (var db = data.CreateDbContext())
        {
            var shipment = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
            Assert.Equal(EstadoEnvio.EnDevolucion, shipment.Estado);
            Assert.Equal(intento.VersionEnvio + 1, shipment.Version);
            Assert.Equal(EstadoCustodiaBulto.Repartidor, (await db.Bultos.SingleAsync(x => x.EnvioId == shipment.Id)).Custodia);
            Assert.Equal(1, await db.ParadasRuta.CountAsync(x => x.EnvioId == shipment.Id && !x.Activa));
            Assert.Equal(1, await db.EnvioEventos.CountAsync(x => x.EnvioId == shipment.Id
                && x.Tipo == TipoEventoEnvio.TransicionEstado && x.EstadoAnterior == "NoEntregado"
                && x.EstadoNuevo == "EnDevolucion" && x.OrigenTipo == "DecisionDevolucionEnvio"));
            Assert.Equal(1L, await db.Database.SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM public.\"DecisionesDevolucionEnvio\" WHERE \"OperadorId\" = {0} AND \"EnvioId\" = {1}",
                shipment.OperadorId, shipment.Id).SingleAsync());
            Assert.Equal(outboxAntes + 1, await db.OutboxMessages.CountAsync(x => x.EnvioId == shipment.Id
                && x.Tipo == OutboxMessageTypes.DeliveryChanged));
        }

        using var replay = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", comando);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayJson.RootElement.GetProperty("repetida").GetBoolean());
        Assert.Equal("EnDevolucion", replayJson.RootElement.GetProperty("estadoActual").GetString());
        await using var afterReplay = data.CreateDbContext();
        Assert.Equal(transicionesAntes + 1, await afterReplay.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado));
        Assert.Equal(outboxAntes + 1, await afterReplay.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId));
        using var changedCommand = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion",
            new { comando.VersionEnvio, comando.ComandoId, Motivo = "Motivo distinto" });
        Assert.Equal(HttpStatusCode.Conflict, changedCommand.StatusCode);
        using var distinctCommand = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion",
            new
            {
                VersionEnvio = replayJson.RootElement.GetProperty("versionActual").GetInt64(),
                ComandoId = Guid.NewGuid(),
                Motivo = "Otra decisión no idempotente"
            });
        Assert.Equal(HttpStatusCode.Conflict, distinctCommand.StatusCode);
        Assert.Equal(transicionesAntes + 1, await afterReplay.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado));
        Assert.Equal(outboxAntes + 1, await afterReplay.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId));
        await using (var restricted = new NpgsqlConnection(runtime.ConnectionString))
        {
            await restricted.OpenAsync();
            foreach (var statement in new[]
            {
                "UPDATE public.\"DecisionesDevolucionEnvio\" SET \"Motivo\"='alterado'",
                "DELETE FROM public.\"DecisionesDevolucionEnvio\""
            })
            {
                await using var commandWrite = new NpgsqlCommand(statement, restricted);
                var forbidden = await Assert.ThrowsAsync<PostgresException>(() => commandWrite.ExecuteNonQueryAsync());
                Assert.Equal("42501", forbidden.SqlState);
            }
        }
        Assert.Equal(1, await afterReplay.DecisionesDevolucionEnvio.CountAsync(x => x.EnvioId == parada.EnvioId));
    }

    [Fact]
    public async Task DespachadorDedicadoPuedeDecidirYRolesSinCorrespondenciaNoPuedenRepetirla()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var intento = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", intento.Resultado);

        Guid dispatcherAccessId;
        Guid userOperatorAccessId;
        Guid customAccessId;
        Guid noPermissionAccessId;
        await using (var db = data.CreateDbContext())
        {
            var operatorId = (await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null)).OperadorId!.Value;
            var dispatcherRole = await db.Roles.SingleAsync(x => x.OperadorId == operatorId && x.Codigo == "despachador_operador");
            Assert.True(dispatcherRole.EsProtegidoSistema);
            Assert.Contains(await db.RolesPermisos.Where(x => x.RolId == dispatcherRole.Id)
                .Join(db.Permisos, grant => grant.PermisoId, permission => permission.Id, (_, permission) => permission.Codigo)
                .ToArrayAsync(), x => x == "envios.gestionar");
            var dispatchUser = new Usuario("dispatch-dec05@example.test", AuthenticationTestData.Hash("dispatch-dec05@example.test"));
            var dispatchAccess = new AccesoUsuario(dispatchUser.Id, operatorId, null, dispatcherRole);
            var userOperatorRole = await db.Roles.SingleAsync(x => x.OperadorId == operatorId && x.Codigo == "usuario_operador");
            var userOperator = new Usuario("user-operator-dec05@example.test", AuthenticationTestData.Hash("user-operator-dec05@example.test"));
            var userOperatorAccess = new AccesoUsuario(userOperator.Id, operatorId, null, userOperatorRole);
            var customRole = new Rol("dec05-custom-manager", "Custom manager", operatorId, null);
            var customUser = new Usuario("custom-manager-dec05@example.test", AuthenticationTestData.Hash("custom-manager-dec05@example.test"));
            var customAccess = new AccesoUsuario(customUser.Id, operatorId, null, customRole);
            var managePermission = await db.Permisos.SingleAsync(x => x.Codigo == "envios.gestionar");
            if (!await db.RolesPermisos.AnyAsync(x => x.RolId == userOperatorRole.Id && x.PermisoId == managePermission.Id))
                db.Add(new RolPermiso(userOperatorRole.Id, managePermission.Id));
            var blockedRole = new Rol("dec05-read-only", "Read-only operator", operatorId, null);
            var blockedUser = new Usuario("blocked-dec05@example.test", AuthenticationTestData.Hash("blocked-dec05@example.test"));
            var blockedAccess = new AccesoUsuario(blockedUser.Id, operatorId, null, blockedRole);
            db.AddRange(dispatchUser, dispatchAccess, userOperator, userOperatorAccess, customRole, customUser, customAccess,
                blockedRole, blockedUser, blockedAccess);
            db.Add(new RolPermiso(customRole.Id, managePermission.Id));
            await db.SaveChangesAsync();
            dispatcherAccessId = dispatchAccess.Id;
            userOperatorAccessId = userOperatorAccess.Id;
            customAccessId = customAccess.Id;
            noPermissionAccessId = blockedAccess.Id;
        }

        var command = new { VersionEnvio = intento.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Devolución por gestión" };
        async Task<HttpStatusCode> PostAsAsync(Guid accessId)
        {
            using var actorFactory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
            using var actorClient = actorFactory.CreateHttpClient();
            await actorClient.SetBearerForAccessAsync(data, accessId);
            using var result = await actorClient.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command);
            return result.StatusCode;
        }

        using (var dispatcherFactory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings()))
        using (var dispatcherClient = dispatcherFactory.CreateHttpClient())
        {
            await dispatcherClient.SetBearerForAccessAsync(data, dispatcherAccessId);
            using var authorized = await dispatcherClient.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command);
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            using var replay = await dispatcherClient.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            await using (var db = data.CreateDbContext())
            {
                var access = await db.AccesosUsuario.SingleAsync(x => x.Id == dispatcherAccessId);
                access.CambiarEstado(false);
                await db.SaveChangesAsync();
            }
            using var revokedAccessReplay = await dispatcherClient.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command);
            Assert.Equal(HttpStatusCode.Unauthorized, revokedAccessReplay.StatusCode);
            await using (var db = data.CreateDbContext())
            {
                var access = await db.AccesosUsuario.SingleAsync(x => x.Id == dispatcherAccessId);
                access.CambiarEstado(true);
                var permissionId = await db.Permisos.Where(p => p.Codigo == "envios.gestionar").Select(p => p.Id).SingleAsync();
                var grant = await db.RolesPermisos.SingleAsync(x => x.RolId == access.RolId && x.PermisoId == permissionId);
                db.RolesPermisos.Remove(grant);
                await db.SaveChangesAsync();
            }
            await dispatcherClient.SetBearerForAccessAsync(data, dispatcherAccessId);
            using var revokedPermissionReplay = await dispatcherClient.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command);
            Assert.Equal(HttpStatusCode.Forbidden, revokedPermissionReplay.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsAsync(userOperatorAccessId));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsAsync(customAccessId));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsAsync(noPermissionAccessId));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsAsync(await AccessId(data, comercio: true)));
        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.EnDevolucion, (await verify.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.Single(await verify.DecisionesDevolucionEnvio.Where(x => x.EnvioId == parada.EnvioId).ToArrayAsync());
        Assert.Single(await verify.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado && x.OrigenTipo == "DecisionDevolucionEnvio").ToArrayAsync());
    }

    [Fact]
    public async Task OperadorConPermisoNoPuedeIniciarDevolucionDeEnvioAjeno()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var intento = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", intento.Resultado);

        Guid foreignAccessId;
        await using (var db = data.CreateDbContext())
        {
            var foreignOperator = new Operador("Operador ajeno a la devolución");
            db.Operadores.Add(foreignOperator);
            await db.SaveChangesAsync();

            var foreignRole = new Rol("despachador_operador", "Dispatcher", foreignOperator.Id, null);
            foreignRole.ProtegerComoRolSistema();
            var foreignPermission = await db.Permisos.SingleAsync(x => x.Codigo == "envios.gestionar");
            var foreignUser = new Usuario("foreign-dispatch-dec05@example.test", AuthenticationTestData.Hash("foreign-dispatch-dec05@example.test"));
            db.AddRange(foreignRole, new RolPermiso(foreignRole.Id, foreignPermission.Id), foreignUser);
            await db.SaveChangesAsync();
            var foreignAccess = new AccesoUsuario(foreignUser.Id, foreignOperator.Id, null, foreignRole);
            db.AccesosUsuario.Add(foreignAccess);
            await db.SaveChangesAsync();
            foreignAccessId = foreignAccess.Id;
        }

        await client.SetBearerForAccessAsync(data, foreignAccessId);
        using var response = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion",
            new { VersionEnvio = intento.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Devolución no autorizada" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.NoEntregado, (await verify.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.Empty(await verify.DecisionesDevolucionEnvio.Where(x => x.EnvioId == parada.EnvioId).ToArrayAsync());
        Assert.Empty(await verify.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado && x.OrigenTipo == "DecisionDevolucionEnvio").ToArrayAsync());
    }

    [Fact]
    public async Task IntentosDisponiblesYPoliticaAutomaticaNoPermitenDecisionManual()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 2);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", fallido.Resultado);
        using var quedanIntentos = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion",
            new { VersionEnvio = fallido.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "No agotado" });
        Assert.Equal(HttpStatusCode.Conflict, quedanIntentos.StatusCode);
        await using (var db = data.CreateDbContext())
        {
            Assert.Empty(await db.DecisionesDevolucionEnvio.ToArrayAsync());
            Assert.Equal(EstadoEnvio.NoEntregado, (await db.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        }

        var automaticData = await postgres.CreateDatabaseAsync();
        using (var bootstrap = automaticData.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(automaticData, maximo: 1,
            politica: PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente);
        var automaticRuntime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(automaticData);
        using var automaticFactory = new IntegrationApiFactory(automaticRuntime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var automaticClient = automaticFactory.CreateHttpClient();
        var automaticRoute = await Cargada(automaticData, automaticClient);
        var automaticStop = automaticRoute.Paradas[0];
        var agotado = await Aplicar(automaticClient, automaticRoute, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = automaticStop.EnvioId,
            VersionEnvio = automaticStop.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
        });
        Assert.Equal("Aplicada", agotado.Resultado);
        using var automaticDecision = await automaticClient.PostAsJsonAsync($"/api/envios/{automaticStop.EnvioId}/iniciar-devolucion",
            new { VersionEnvio = agotado.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Ya inició automáticamente" });
        Assert.Equal(HttpStatusCode.Conflict, automaticDecision.StatusCode);
        await using var automaticVerify = automaticData.CreateDbContext();
        Assert.Equal(EstadoEnvio.EnDevolucion, (await automaticVerify.Envios.SingleAsync(x => x.Id == automaticStop.EnvioId)).Estado);
        Assert.Empty(await automaticVerify.DecisionesDevolucionEnvio.ToArrayAsync());
    }

    [Fact]
    public async Task ReplayAutorizadoDevuelveDecisionOriginalAunqueElEnvioYaEsteCancelado()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
        });
        var command = new { VersionEnvio = fallido.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Devolución solicitada" };
        using var first = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var cancellation = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/cancelacion",
            new { Version = fallido.VersionEnvio + 1, Motivo = "Cancelación posterior" });
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        int historyAfterCancellation;
        int outboxAfterCancellation;
        await using (var db = data.CreateDbContext())
        {
            historyAfterCancellation = await db.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId);
            outboxAfterCancellation = await db.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId);
        }

        using var replay = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EnDevolucion", result.GetProperty("estadoDecision").GetString());
        Assert.Equal("Cancelado", result.GetProperty("estadoActual").GetString());
        Assert.True(result.GetProperty("repetida").GetBoolean());
        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.Cancelado, (await verify.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.Equal(1, await verify.DecisionesDevolucionEnvio.CountAsync(x => x.EnvioId == parada.EnvioId));
        Assert.Equal(historyAfterCancellation, await verify.EnvioEventos.CountAsync(x => x.EnvioId == parada.EnvioId));
        Assert.Equal(outboxAfterCancellation, await verify.OutboxMessages.CountAsync(x => x.EnvioId == parada.EnvioId));
    }

    [Fact]
    public async Task DecisionesConcurrentesDeDevolucionConfirmanUnaSolaTransicion()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
        });
        var respuestas = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion",
                new { VersionEnvio = fallido.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Decisión A" }),
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion",
                new { VersionEnvio = fallido.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Decisión B" }));
        try
        {
            Assert.Equal(HttpStatusCode.OK, Assert.Single(respuestas, x => x.StatusCode == HttpStatusCode.OK).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, Assert.Single(respuestas, x => x.StatusCode == HttpStatusCode.Conflict).StatusCode);
        }
        finally { foreach (var response in respuestas) response.Dispose(); }
        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.EnDevolucion, (await verify.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.Single(await verify.DecisionesDevolucionEnvio.Where(x => x.EnvioId == parada.EnvioId).ToArrayAsync());
        Assert.Single(await verify.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.EstadoAnterior == "NoEntregado" && x.EstadoNuevo == "EnDevolucion").ToArrayAsync());
    }

    [Fact]
    public async Task MismoComandoConcurrenteDevuelveUnResultadoAplicadoYUnReplay()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
        });
        var command = new { VersionEnvio = fallido.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Comando simultáneo" };
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command),
            client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion", command));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Single(bodies, body => !body.GetProperty("repetida").GetBoolean());
            Assert.Single(bodies, body => body.GetProperty("repetida").GetBoolean());
            Assert.All(bodies, body => Assert.Equal("EnDevolucion", body.GetProperty("estadoDecision").GetString()));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var verify = data.CreateDbContext();
        Assert.Single(await verify.DecisionesDevolucionEnvio.Where(x => x.EnvioId == parada.EnvioId).ToArrayAsync());
        Assert.Single(await verify.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.EstadoAnterior == "NoEntregado" && x.EstadoNuevo == "EnDevolucion").ToArrayAsync());
    }

    [Fact]
    public async Task FalloAlInsertarHistoriaRevierteDecisionEstadoParadaAvisoYOutbox()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var fallido = await Aplicar(client, hoja, new
        {
            Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = parada.EnvioId,
            VersionEnvio = parada.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
        });
        await using (var owner = new Npgsql.NpgsqlConnection(data.ConnectionString))
        {
            await owner.OpenAsync();
            await using var command = owner.CreateCommand();
            command.CommandText = """
                CREATE FUNCTION public.test_reject_dec05_history() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."OrigenTipo" = 'DecisionDevolucionEnvio' THEN
                        RAISE EXCEPTION 'Injected history failure for DEC-05 rollback test';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER test_reject_dec05_history
                    BEFORE INSERT ON public."EnvioEventos"
                    FOR EACH ROW EXECUTE FUNCTION public.test_reject_dec05_history();
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(async () =>
        {
            using var response = await client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/iniciar-devolucion",
                new { VersionEnvio = fallido.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Prueba de rollback" });
        });
        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.NoEntregado, (await verify.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.True((await verify.ParadasRuta.SingleAsync(x => x.EnvioId == parada.EnvioId)).Activa);
        Assert.Empty(await verify.DecisionesDevolucionEnvio.ToArrayAsync());
        Assert.Empty(await verify.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado && x.EstadoAnterior == "NoEntregado"
            && x.EstadoNuevo == "EnDevolucion").ToArrayAsync());
        Assert.Empty(await verify.AvisosReparto.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == "DevolucionIniciada").ToArrayAsync());
        Assert.Empty(await verify.OutboxMessages.Where(x => x.EnvioId == parada.EnvioId
            && x.Tipo == OutboxMessageTypes.DeliveryChanged).ToArrayAsync());
    }
}
