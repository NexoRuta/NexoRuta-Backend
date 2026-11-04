using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task RestrictedApiRoleCannotPersistAnUndocumentedShipmentTransition()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var ownerApi = data.CreateApiFactory();
        using var ownerClient = ownerApi.CreateHttpClient();
        var route = await Lista(data, ownerClient);
        var shipmentId = Assert.Single(route.Paradas).EnvioId;
        Guid operatorId;
        Guid userId;
        Guid accessId;
        await using (var owner = data.CreateDbContext())
        {
            var shipment = await owner.Envios.SingleAsync(x => x.Id == shipmentId);
            operatorId = shipment.OperadorId;
            var access = await owner.AccesosUsuario.SingleAsync(x => x.OperadorId == operatorId && x.ComercioId == null);
            userId = access.UsuarioId;
            accessId = access.Id;
        }

        var restricted = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        await using var api = restricted.CreateDbContext();
        await using var transaction = await api.BeginOrJoinAsync();
        await api.ApplyAsync(new RlsActorContext(RlsActorKind.Operador, userId, accessId, operatorId,
            OrganizationType: TipoAccesoUsuario.Operador));
        Assert.True(await api.Database.SqlQueryRaw<bool>("""
            SELECT has_column_privilege(current_user, 'public."Envios"', 'Estado', 'UPDATE') AS "Value"
            """).SingleAsync());

        const string target = "EnDeposito";
        var failure = await Assert.ThrowsAsync<PostgresException>(() => api.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.\"Envios\" SET \"Estado\"={target}, \"Version\"=\"Version\"+1 WHERE \"Id\"={shipmentId}"));

        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("CK_Envios_TransicionEstado", failure.ConstraintName);
        // The rejected UPDATE aborts PostgreSQL's transaction; disposing the lease rolls it back.
    }

    [Fact]
    public async Task OfflineDeliveryAfterReprogrammingRemainsAConflictAndCannotAdvanceShipment()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        var route = await Cargada(data, client);
        var stop = Assert.Single(route.Paradas);
        var transitVersion = stop.EnvioVersion;
        var attempt = await Aplicar(client, route, Operacion(Guid.NewGuid(), 4, "IntentoFallido", stop.EnvioId,
            transitVersion, new { Motivo = "AUSENTE" }));
        Assert.Equal("Aplicada", attempt.Resultado);
        using var reschedule = await client.PostAsJsonAsync($"/api/envios/{stop.EnvioId}/reprogramacion",
            new { VersionEnvio = attempt.VersionEnvio });
        Assert.Equal(HttpStatusCode.OK, reschedule.StatusCode);

        var late = await Aplicar(client, route, Operacion(Guid.NewGuid(), 5, "EntregaFisica", stop.EnvioId,
            transitVersion, new { Nombre = "Hecho offline recibido después" }));

        Assert.Equal("Conflicto", late.Resultado);
        await using var verify = data.CreateDbContext();
        var shipment = await verify.Envios.SingleAsync(x => x.Id == stop.EnvioId);
        Assert.Equal(EstadoEnvio.Reprogramado, shipment.Estado);
        Assert.Null(shipment.EntregaFisicaEnUtc);
        Assert.Equal(EstadoCustodiaBulto.Repartidor, (await verify.Bultos.SingleAsync(x => x.EnvioId == stop.EnvioId)).Custodia);
        var oldStop = await verify.ParadasRuta.SingleAsync(x => x.RutaId == route.Id && x.EnvioId == stop.EnvioId);
        Assert.False(oldStop.Activa);
        Assert.Equal("Reprogramado tras un intento fallido.", oldStop.MotivoSuspension);
        var persisted = await verify.OperacionesJornada.SingleAsync(x => x.RutaId == route.Id && x.Secuencia == 5);
        Assert.Equal("Conflicto", persisted.Resultado);
        Assert.Contains("Hecho offline recibido después", persisted.Payload);
        Assert.Empty(await verify.EnvioEventos.Where(x => x.EnvioId == stop.EnvioId && x.Tipo == "TransicionEstado"
            && (x.EstadoNuevo == "Entregado" || x.EstadoNuevo == "EntregaPendienteValidacion")).ToArrayAsync());
    }

    [Fact]
    public async Task FinJornadaLeavesAnUnstartedAssignmentAndItCanBeReassignedWithoutCustodyTransfer()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var route = await Lista(data, client);
        var stop = Assert.Single(route.Paradas);
        Assert.Equal("Aplicada", (await Aplicar(client, route, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);

        var close = await Aplicar(client, route, Operacion(Guid.NewGuid(), 2, "FinJornada"));
        Assert.Equal("Aplicada", close.Resultado);
        Assert.Contains("asignados sin salida", close.Detalle, StringComparison.OrdinalIgnoreCase);
        await using (var verify = data.CreateDbContext())
        {
            var shipment = await verify.Envios.SingleAsync(x => x.Id == stop.EnvioId);
            Assert.Equal(EstadoEnvio.AsignadoRuta, shipment.Estado);
            Assert.Equal(stop.EnvioVersion, shipment.Version);
            Assert.Equal(EstadoCustodiaBulto.Deposito, (await verify.Bultos.SingleAsync(x => x.EnvioId == stop.EnvioId)).Custodia);
            var oldStop = await verify.ParadasRuta.SingleAsync(x => x.RutaId == route.Id && x.EnvioId == stop.EnvioId);
            Assert.False(oldStop.Activa);
            Assert.Contains("jornada cerrada antes de la salida", oldStop.MotivoSuspension!, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await verify.EnvioEventos.Where(x => x.EnvioId == stop.EnvioId && x.Tipo == "TransicionEstado"
                && x.EstadoAnterior == "AsignadoRuta" && x.EstadoNuevo == "EnDeposito").ToArrayAsync());
        }

        using var destinationResponse = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = route.RepartidorAccesoId,
            VehiculoId = route.VehiculoId,
            Fecha = route.Fecha.AddDays(1),
            Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, destinationResponse.StatusCode);
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var reassignment = await client.PostAsJsonAsync($"/api/envios/{stop.EnvioId}/reasignacion", new
        {
            RutaDestinoId = destination.Id,
            VersionEnvio = stop.EnvioVersion,
            VersionRutaDestino = destination.Version,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1).AddHours(1),
            Motivo = "Replanificar antes de despachar."
        });
        Assert.Equal(HttpStatusCode.OK, reassignment.StatusCode);
        await using var final = data.CreateDbContext();
        var finalShipment = await final.Envios.SingleAsync(x => x.Id == stop.EnvioId);
        Assert.Equal(EstadoEnvio.AsignadoRuta, finalShipment.Estado);
        Assert.Equal(EstadoCustodiaBulto.Deposito, (await final.Bultos.SingleAsync(x => x.EnvioId == stop.EnvioId)).Custodia);
        Assert.Single(await final.ParadasRuta.Where(x => x.RutaId == destination.Id && x.EnvioId == stop.EnvioId && x.Activa).ToArrayAsync());
    }

    [Fact]
    public async Task ConfirmarCargaConDiscrepanciaPermitidaSuspendeSinRetrocederEstado()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var bootstrap = data.CreateApiFactory()) using (bootstrap.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var prepared = await Preparar(data, client, bloquearDiscrepanciasCarga: false);
        using var planResponse = await client.PostAsJsonAsync("/api/rutas", Solicitud(prepared));
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
        var planned = (await planResponse.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var dispatch = await client.PostAsJsonAsync($"/api/rutas/{planned.Id}/despacho", new { planned.Version });
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        var route = (await dispatch.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("Aplicada", (await Aplicar(client, route, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);

        var load = await Aplicar(client, route, Operacion(Guid.NewGuid(), 2, "ConfirmarCarga"));

        Assert.Equal("Aplicada", load.Resultado);
        Assert.Contains("1 envío(s) suspendidos", load.Detalle, StringComparison.OrdinalIgnoreCase);
        await using var verify = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.AsignadoRuta, (await verify.Envios.SingleAsync(x => x.Id == prepared.EnvioId)).Estado);
        Assert.Equal(EstadoCustodiaBulto.Deposito, (await verify.Bultos.SingleAsync(x => x.EnvioId == prepared.EnvioId)).Custodia);
        var stop = await verify.ParadasRuta.SingleAsync(x => x.RutaId == route.Id && x.EnvioId == prepared.EnvioId);
        Assert.False(stop.Activa);
        Assert.Contains("no se cambió el estado comercial", stop.MotivoSuspension!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await verify.EnvioEventos.Where(x => x.EnvioId == prepared.EnvioId && x.Tipo == "TransicionEstado"
            && x.EstadoAnterior == "AsignadoRuta" && x.EstadoNuevo == "EnDeposito").ToArrayAsync());
    }
}
