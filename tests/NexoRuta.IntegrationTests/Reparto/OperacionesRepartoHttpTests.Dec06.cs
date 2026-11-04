using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
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
    public async Task AdministradorConfirmaExtravioDesdeDepositoSinMoverCustodia()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client, confirmar: false);
        var before = await Snapshot(data, preparado.EnvioId);
        var command = new { VersionEnvio = before.Version, ComandoId = Guid.NewGuid(), Motivo = "  Caja extraviada en el depósito  " };

        using (var missingVersion = await client.PostAsJsonAsync($"/api/envios/{preparado.EnvioId}/confirmar-extravio",
            new { ComandoId = Guid.NewGuid(), Motivo = "Missing expected version" }))
            Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode);
        Assert.Equal(before, await Snapshot(data, preparado.EnvioId));

        using var response = await client.PostAsJsonAsync($"/api/envios/{preparado.EnvioId}/confirmar-extravio", command);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Extraviado", json.RootElement.GetProperty("estadoConfirmado").GetString());
        Assert.Equal("Caja extraviada en el depósito", json.RootElement.GetProperty("motivo").GetString());
        Assert.False(json.RootElement.GetProperty("repetida").GetBoolean());
        var after = await Snapshot(data, preparado.EnvioId);
        Assert.Equal(EstadoEnvio.Extraviado, after.Estado);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(before.Custodia, after.Custodia);

        int transitions;
        int deliveryOutbox;
        await using (var db = data.CreateDbContext())
        {
            var confirmation = await db.ConfirmacionesExtravioEnvio.SingleAsync(x => x.EnvioId == preparado.EnvioId);
            Assert.Equal(command.ComandoId, confirmation.ComandoId);
            Assert.Equal(command.Motivo.Trim(), confirmation.Motivo);
            Assert.Equal(before.Version, confirmation.VersionAnterior);
            Assert.Equal(after.Version, confirmation.VersionNueva);
            Assert.NotEqual(Guid.Empty, confirmation.AccesoId);
            transitions = await db.EnvioEventos.CountAsync(x => x.EnvioId == preparado.EnvioId
                && x.Tipo == TipoEventoEnvio.TransicionEstado);
            Assert.Equal(1, await db.EnvioEventos.CountAsync(x => x.EnvioId == preparado.EnvioId
                && x.Tipo == TipoEventoEnvio.TransicionEstado && x.OrigenTipo == "ConfirmacionExtravioEnvio"
                && x.EstadoAnterior == "EnDeposito" && x.EstadoNuevo == "Extraviado"));
            deliveryOutbox = await db.OutboxMessages.CountAsync(x => x.EnvioId == preparado.EnvioId
                && x.Tipo == OutboxMessageTypes.DeliveryChanged);
        }

        using var replay = await client.PostAsJsonAsync($"/api/envios/{preparado.EnvioId}/confirmar-extravio", command);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = System.Text.Json.JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayJson.RootElement.GetProperty("repetida").GetBoolean());
        Assert.Equal("Extraviado", replayJson.RootElement.GetProperty("estadoActual").GetString());

        using var reusedCommand = await client.PostAsJsonAsync($"/api/envios/{preparado.EnvioId}/confirmar-extravio",
            new { command.VersionEnvio, command.ComandoId, Motivo = "otro motivo" });
        Assert.Equal(HttpStatusCode.Conflict, reusedCommand.StatusCode);
        using var distinctCommand = await client.PostAsJsonAsync($"/api/envios/{preparado.EnvioId}/confirmar-extravio",
            new { VersionEnvio = after.Version, ComandoId = Guid.NewGuid(), Motivo = "otro comando" });
        Assert.Equal(HttpStatusCode.Conflict, distinctCommand.StatusCode);
        using var invalidReason = await client.PostAsJsonAsync($"/api/envios/{preparado.EnvioId}/confirmar-extravio",
            new { VersionEnvio = after.Version, ComandoId = Guid.NewGuid(), Motivo = " " });
        Assert.Equal(HttpStatusCode.BadRequest, invalidReason.StatusCode);

        await using (var db = data.CreateDbContext())
        {
            Assert.Single(await db.ConfirmacionesExtravioEnvio.Where(x => x.EnvioId == preparado.EnvioId).ToArrayAsync());
            Assert.Equal(transitions, await db.EnvioEventos.CountAsync(x => x.EnvioId == preparado.EnvioId
                && x.Tipo == TipoEventoEnvio.TransicionEstado));
            Assert.Equal(deliveryOutbox, await db.OutboxMessages.CountAsync(x => x.EnvioId == preparado.EnvioId
                && x.Tipo == OutboxMessageTypes.DeliveryChanged));
        }

        await using (var restricted = new NpgsqlConnection(runtime.ConnectionString))
        {
            await restricted.OpenAsync();
            foreach (var statement in new[]
            {
                "UPDATE public.\"ConfirmacionesExtravioEnvio\" SET \"Motivo\"='alterado'",
                "DELETE FROM public.\"ConfirmacionesExtravioEnvio\""
            })
            {
                await using var commandWrite = new NpgsqlCommand(statement, restricted);
                var forbidden = await Assert.ThrowsAsync<PostgresException>(() => commandWrite.ExecuteNonQueryAsync());
                Assert.Equal("42501", forbidden.SqlState);
            }
        }
    }

    [Fact]
    public async Task DispatcherConfirmsLossButCannotCancelAndReplaysRecheckCurrentAccess()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        using var factory = new IntegrationApiFactory(runtime, settings: settings);
        using var client = factory.CreateHttpClient();
        var prepared = await Preparar(data, client, confirmar: false);
        Guid dispatcherAccessId;
        Guid customAccessId;
        Guid userOperatorAccessId;
        Guid ownerAccessId;
        Guid courierAccessId;
        Guid foreignAccessId;
        await using (var db = data.CreateDbContext())
        {
            var operatorId = (await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null)).OperadorId!.Value;
            var dispatcherRole = await db.Roles.SingleAsync(x => x.OperadorId == operatorId
                && x.Codigo == "despachador_operador" && x.EsProtegidoSistema);
            var managingPermission = await db.Permisos.SingleAsync(x => x.Codigo == "envios.gestionar");
            var queryPermission = await db.Permisos.SingleAsync(x => x.Codigo == "envios.consultar");
            var courierPermission = await db.Permisos.SingleAsync(x => x.Codigo == "reparto.ejecutar");
            var ownerAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId == operatorId && x.EsPropietario);
            var dispatcher = NewOperatorAccess("dec06-dispatch@example.test", operatorId, dispatcherRole);
            var customRole = new Rol("dec06-custom", "Custom manager", operatorId, null);
            var custom = NewOperatorAccess("dec06-custom@example.test", operatorId, customRole);
            var userOperatorRole = await db.Roles.SingleAsync(x => x.OperadorId == operatorId && x.Codigo == "usuario_operador");
            var userOperator = NewOperatorAccess("dec06-user-operator@example.test", operatorId, userOperatorRole);
            var courierRole = new Rol("dec06-courier", "Courier", operatorId, null);
            var courier = NewOperatorAccess("dec06-courier@example.test", operatorId, courierRole);
            var foreignOperator = new Operador("Foreign operator for DEC-06");
            var foreignRole = new Rol("despachador_operador", "Dispatcher", foreignOperator.Id, null);
            foreignRole.ProtegerComoRolSistema();
            var foreign = NewOperatorAccess("dec06-foreign@example.test", foreignOperator.Id, foreignRole);
            db.AddRange(dispatcher.User, customRole, custom.User, userOperator.User, courierRole, courier.User,
                foreignOperator, foreignRole, foreign.User);
            await db.SaveChangesAsync();
            db.AddRange(dispatcher.Access, custom.Access, userOperator.Access, courier.Access, foreign.Access);
            if (!await db.RolesPermisos.AnyAsync(x => x.RolId == userOperatorRole.Id && x.PermisoId == managingPermission.Id))
                db.RolesPermisos.Add(new RolPermiso(userOperatorRole.Id, managingPermission.Id));
            db.AddRange(new RolPermiso(customRole.Id, managingPermission.Id),
                new RolPermiso(courierRole.Id, managingPermission.Id),
                new RolPermiso(courierRole.Id, courierPermission.Id),
                new RolPermiso(foreignRole.Id, managingPermission.Id),
                new RolPermiso(foreignRole.Id, queryPermission.Id));
            await db.SaveChangesAsync();
            dispatcherAccessId = dispatcher.Access.Id;
            customAccessId = custom.Access.Id;
            userOperatorAccessId = userOperator.Access.Id;
            ownerAccessId = ownerAccess.Id;
            courierAccessId = courier.Access.Id;
            foreignAccessId = foreign.Access.Id;
        }

        await client.SetBearerForAccessAsync(data, dispatcherAccessId);
        using (var identity = await client.GetAsync("/api/usuarios/actual"))
        {
            Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
            using var body = System.Text.Json.JsonDocument.Parse(await identity.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("puedeGestionarDecisionesOperativasEnvio").GetBoolean());
        }
        var before = await Snapshot(data, prepared.EnvioId);
        var command = new { VersionEnvio = before.Version, ComandoId = Guid.NewGuid(), Motivo = "Robo reportado en depósito" };
        using var dispatcherCancel = await client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/cancelacion",
            new { Version = before.Version, Motivo = "El rol de despachador no cancela" });
        Assert.Equal(HttpStatusCode.Forbidden, dispatcherCancel.StatusCode);
        using var confirmed = await client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio", command);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using var replay = await client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio", command);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        async Task<HttpStatusCode> TryAsAsync(Guid accessId)
        {
            using var actorFactory = new IntegrationApiFactory(runtime, settings: settings);
            using var actorClient = actorFactory.CreateHttpClient();
            await actorClient.SetBearerForAccessAsync(data, accessId);
            using var response = await actorClient.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio", command);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await TryAsAsync(customAccessId));
        Assert.Equal(HttpStatusCode.Forbidden, await TryAsAsync(userOperatorAccessId));
        Assert.Equal(HttpStatusCode.Forbidden, await TryAsAsync(courierAccessId));
        Assert.Equal(HttpStatusCode.NotFound, await TryAsAsync(foreignAccessId));
        Assert.Equal(HttpStatusCode.Forbidden, await TryAsAsync(await AccessId(data, comercio: true)));

        await using (var db = data.CreateDbContext())
        {
            var owner = await db.AccesosUsuario.SingleAsync(x => x.Id == ownerAccessId);
            owner.AsignarRol(await db.Roles.SingleAsync(x => x.OperadorId == owner.OperadorId
                && x.Codigo == "usuario_operador"));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, await TryAsAsync(ownerAccessId));

        await using (var db = data.CreateDbContext())
        {
            var access = await db.AccesosUsuario.SingleAsync(x => x.Id == dispatcherAccessId);
            access.CambiarEstado(false);
            await db.SaveChangesAsync();
        }
        using var revokedAccessReplay = await client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio", command);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedAccessReplay.StatusCode);
        await using (var db = data.CreateDbContext())
        {
            var access = await db.AccesosUsuario.SingleAsync(x => x.Id == dispatcherAccessId);
            access.CambiarEstado(true);
            var permissionId = await db.Permisos.Where(x => x.Codigo == "envios.gestionar").Select(x => x.Id).SingleAsync();
            db.RolesPermisos.Remove(await db.RolesPermisos.SingleAsync(x => x.RolId == access.RolId && x.PermisoId == permissionId));
            await db.SaveChangesAsync();
        }
        await client.SetBearerForAccessAsync(data, dispatcherAccessId);
        using var revokedPermissionReplay = await client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio", command);
        Assert.Equal(HttpStatusCode.Forbidden, revokedPermissionReplay.StatusCode);
        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.Extraviado, (await verify.Envios.SingleAsync(x => x.Id == prepared.EnvioId)).Estado);
        Assert.Single(await verify.ConfirmacionesExtravioEnvio.Where(x => x.EnvioId == prepared.EnvioId).ToArrayAsync());
        Assert.Single(await verify.EnvioEventos.Where(x => x.EnvioId == prepared.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado && x.OrigenTipo == "ConfirmacionExtravioEnvio").ToArrayAsync());
    }

    [Theory]
    [InlineData("EnDeposito")]
    [InlineData("AsignadoRuta")]
    [InlineData("EnTransito")]
    [InlineData("EnDevolucion")]
    public async Task ConfirmationWorksFromEachAllowedOriginAndSuspendsOnlyAnActiveStop(string origin)
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        if (origin == "EnDevolucion") await PublicarReglasDeReprogramacion(data, maximo: 1);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        Guid shipmentId;
        HojaRutaResponse? transitRoute = null;
        switch (origin)
        {
            case "EnDeposito":
                shipmentId = (await Preparar(data, client, confirmar: false)).EnvioId;
                break;
            case "AsignadoRuta":
                shipmentId = Assert.Single((await Lista(data, client)).Paradas).EnvioId;
                break;
            case "EnTransito":
                transitRoute = await Cargada(data, client);
                shipmentId = Assert.Single(transitRoute.Paradas).EnvioId;
                break;
            case "EnDevolucion":
                var route = await Cargada(data, client);
                var stop = Assert.Single(route.Paradas);
                var failed = await Aplicar(client, route, new
                {
                    Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = stop.EnvioId,
                    VersionEnvio = stop.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { Motivo = "AUSENTE" }
                });
                using (var returnDecision = await client.PostAsJsonAsync($"/api/envios/{stop.EnvioId}/iniciar-devolucion",
                    new { VersionEnvio = failed.VersionEnvio, ComandoId = Guid.NewGuid(), Motivo = "Intentos agotados" }))
                    Assert.Equal(HttpStatusCode.OK, returnDecision.StatusCode);
                shipmentId = stop.EnvioId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
        }

        var before = await Snapshot(data, shipmentId);
        Assert.Equal(Enum.Parse<EstadoEnvio>(origin), before.Estado);
        var activeStopsBefore = await ActiveStops(data, shipmentId);
        using var response = await client.PostAsJsonAsync($"/api/envios/{shipmentId}/confirmar-extravio",
            new { VersionEnvio = before.Version, ComandoId = Guid.NewGuid(), Motivo = "Pérdida confirmada por administración" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await Snapshot(data, shipmentId);
        Assert.Equal(EstadoEnvio.Extraviado, after.Estado);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(before.Custodia, after.Custodia);
        Assert.Equal(0, await ActiveStops(data, shipmentId));

        await using var verify = data.CreateDbContext();
        var transition = await verify.EnvioEventos.SingleAsync(x => x.EnvioId == shipmentId
            && x.Tipo == TipoEventoEnvio.TransicionEstado && x.OrigenTipo == "ConfirmacionExtravioEnvio");
        Assert.Equal(origin, transition.EstadoAnterior);
        Assert.Equal("Extraviado", transition.EstadoNuevo);
        Assert.Equal(1, await verify.ConfirmacionesExtravioEnvio.CountAsync(x => x.EnvioId == shipmentId));
        if (origin == "EnTransito" && activeStopsBefore == 1)
        {
            Assert.Equal(1, await verify.AvisosReparto.CountAsync(x => x.EnvioId == shipmentId
                && x.Tipo == "ExtravioConfirmado"));
            Assert.Equal(1, await verify.OutboxMessages.CountAsync(x => x.EnvioId == shipmentId
                && x.Tipo == OutboxMessageTypes.DeliveryChanged && x.Destino == OutboxDestino.PushFcm));
        }
        else
        {
            Assert.Empty(await verify.AvisosReparto.Where(x => x.EnvioId == shipmentId
                && x.Tipo == "ExtravioConfirmado").ToArrayAsync());
        }

        if (transitRoute is not null)
        {
            var oldStop = Assert.Single(transitRoute.Paradas);
            var lateDelivery = await Aplicar(client, transitRoute, new
            {
                Id = Guid.NewGuid(), Secuencia = 4, Tipo = "EntregaFisica", EnvioId = shipmentId,
                VersionEnvio = oldStop.EnvioVersion, OcurridaEn = LlegadaEstimada, Datos = new { }
            });
            Assert.Equal("Conflicto", lateDelivery.Resultado);
            Assert.Equal(EstadoEnvio.Extraviado, (await Snapshot(data, shipmentId)).Estado);
        }
    }

    [Fact]
    public async Task ConcurrentLossCommandsProduceOneStateTransitionAndOneConfirmation()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var prepared = await Preparar(data, client, confirmar: false);
        var before = await Snapshot(data, prepared.EnvioId);
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio", new
            {
                VersionEnvio = before.Version, ComandoId = Guid.NewGuid(), Motivo = "Primer motivo"
            }),
            client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio", new
            {
                VersionEnvio = before.Version, ComandoId = Guid.NewGuid(), Motivo = "Segundo motivo"
            }));
        try
        {
            Assert.Equal(HttpStatusCode.OK, Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict).StatusCode);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.Extraviado, (await verify.Envios.SingleAsync(x => x.Id == prepared.EnvioId)).Estado);
        Assert.Single(await verify.ConfirmacionesExtravioEnvio.Where(x => x.EnvioId == prepared.EnvioId).ToArrayAsync());
        Assert.Single(await verify.EnvioEventos.Where(x => x.EnvioId == prepared.EnvioId
            && x.Tipo == TipoEventoEnvio.TransicionEstado && x.OrigenTipo == "ConfirmacionExtravioEnvio").ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentCancellationAndLossConfirmationCannotBothCommit()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var prepared = await Preparar(data, client, confirmar: false);
        var before = await Snapshot(data, prepared.EnvioId);
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio", new
            {
                VersionEnvio = before.Version, ComandoId = Guid.NewGuid(), Motivo = "Pérdida concurrente"
            }),
            client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/cancelacion", new
            {
                Version = before.Version, Motivo = "Cancelación concurrente"
            }));
        try
        {
            var loss = Assert.Single(responses, x => x.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/confirmar-extravio") == true);
            var cancellation = Assert.Single(responses, x => x.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/cancelacion") == true);
            Assert.True(loss.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
            Assert.True(cancellation.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
            Assert.NotEqual(loss.IsSuccessStatusCode, cancellation.IsSuccessStatusCode);
            await using var verify = data.CreateDbContext();
            var shipment = await verify.Envios.SingleAsync(x => x.Id == prepared.EnvioId);
            Assert.True(shipment.Estado is EstadoEnvio.Cancelado or EstadoEnvio.Extraviado);
            Assert.Equal(cancellation.IsSuccessStatusCode,
                await verify.CancelacionesEnvio.AnyAsync(x => x.EnvioId == prepared.EnvioId));
            Assert.Equal(loss.IsSuccessStatusCode,
                await verify.ConfirmacionesExtravioEnvio.AnyAsync(x => x.EnvioId == prepared.EnvioId));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task FailedHistoryInsertRollsBackLossConfirmationShipmentAndOutboxTogether()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var prepared = await Preparar(data, client, confirmar: false);
        var before = await Snapshot(data, prepared.EnvioId);
        int eventsBefore;
        int outboxBefore;
        await using (var db = data.CreateDbContext())
        {
            eventsBefore = await db.EnvioEventos.CountAsync(x => x.EnvioId == prepared.EnvioId);
            outboxBefore = await db.OutboxMessages.CountAsync(x => x.EnvioId == prepared.EnvioId);
        }
        await using (var owner = new NpgsqlConnection(data.ConnectionString))
        {
            await owner.OpenAsync();
            await using var command = owner.CreateCommand();
            command.CommandText = """
                CREATE FUNCTION public.test_reject_dec06_history() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."OrigenTipo" = 'ConfirmacionExtravioEnvio' THEN
                        RAISE EXCEPTION 'Injected DEC-06 history failure';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER test_reject_dec06_history
                    BEFORE INSERT ON public."EnvioEventos"
                    FOR EACH ROW EXECUTE FUNCTION public.test_reject_dec06_history();
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            using var response = await client.PostAsJsonAsync($"/api/envios/{prepared.EnvioId}/confirmar-extravio",
                new { VersionEnvio = before.Version, ComandoId = Guid.NewGuid(), Motivo = "Fallo de historia" });
        });
        var after = await Snapshot(data, prepared.EnvioId);
        Assert.Equal(before, after);
        await using var verify = data.CreateDbContext();
        Assert.Empty(await verify.ConfirmacionesExtravioEnvio.Where(x => x.EnvioId == prepared.EnvioId).ToArrayAsync());
        Assert.Equal(eventsBefore, await verify.EnvioEventos.CountAsync(x => x.EnvioId == prepared.EnvioId));
        Assert.Equal(outboxBefore, await verify.OutboxMessages.CountAsync(x => x.EnvioId == prepared.EnvioId));
    }

    private static async Task<int> ActiveStops(IntegrationDatabase data, Guid shipmentId)
    {
        await using var db = data.CreateDbContext();
        return await db.ParadasRuta.CountAsync(x => x.EnvioId == shipmentId && x.Activa);
    }

    private static (Usuario User, AccesoUsuario Access) NewOperatorAccess(string email, Guid operatorId, Rol role)
    {
        var user = new Usuario(email, AuthenticationTestData.Hash(email));
        return (user, new AccesoUsuario(user.Id, operatorId, null, role));
    }

    private static async Task<(EstadoEnvio Estado, long Version, EstadoCustodiaBulto Custodia)> Snapshot(
        IntegrationDatabase data, Guid envioId)
    {
        await using var db = data.CreateDbContext();
        var envio = await db.Envios.SingleAsync(x => x.Id == envioId);
        var bulto = await db.Bultos.SingleAsync(x => x.EnvioId == envioId);
        return (envio.Estado, envio.Version, bulto.Custodia);
    }
}
