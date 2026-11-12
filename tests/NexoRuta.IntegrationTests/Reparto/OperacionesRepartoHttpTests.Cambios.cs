using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NexoRuta.Domain.Administracion;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task RejectedPickupResolutionPreservesExistingMessageAndBusinessState()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var sheet = await Lista(data, client); var stop = sheet.Paradas.Single();
        await using var db = data.CreateDbContext();
        var before = await db.Bultos.AsNoTracking().SingleAsync(x => x.EnvioId == stop.EnvioId);
        var command = Operacion(Guid.NewGuid(), 1, "RetirarBultos", stop.EnvioId, stop.EnvioVersion,
            new { ResuelveOperacionId = Guid.NewGuid() });
        var ack = await Aplicar(client, sheet, command); Assert.Equal("Conflicto", ack.Resultado);
        var after = await db.Bultos.AsNoTracking().SingleAsync(x => x.Id == before.Id);
        Assert.Equal(before.Version, after.Version); Assert.Equal(before.Custodia, after.Custodia);
        Assert.Equal(JsonSerializer.Serialize(ack), JsonSerializer.Serialize(await Aplicar(client, sheet, command)));
        Assert.Equal("La resolución debe referenciar un retiro rechazado de esta misma visita.", ack.Detalle);
    }

    [Fact]
    public async Task DeliveryResolutionUsesNewLinkedCommandPreservesRejectedReceiptAndCannotDeliverAgain()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var factory = new IntegrationApiFactory(runtime, settings: settings); using var client = factory.CreateHttpClient();
        var ready = await Preparar(data, client); var sheet = await PlanYDespacho(client, ready);
        var stop = sheet.Paradas.Single(); var parcel = stop.Bultos.Single();
        Assert.Equal("Aplicada", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
        var load = await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 2, "CargarBulto", stop.EnvioId, stop.EnvioVersion, new { BultoId = parcel.Id }));
        Assert.Equal("Aplicada", load.Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 3, "ConfirmarCarga"))).Resultado);
        var rejectedId = Guid.NewGuid();
        var rejected = await Aplicar(client, sheet, Operacion(rejectedId, 4, "EntregaBultos", stop.EnvioId, 0,
            new { ResultadosBultos = new[] { new { BultoId = parcel.Id, parcel.Version, Entregado = true } } }));
        Assert.Equal("Conflicto", rejected.Resultado);
        await using var db = data.CreateDbContext();
        var original = await db.OperacionesJornada.AsNoTracking().SingleAsync(o => o.Id == rejectedId);
        var foto = Guid.NewGuid();
        using var upload = await client.PutAsJsonAsync($"/api/mobile/jornadas/{sheet.JornadaId}/evidencias/{foto}",
            new { stop.EnvioId, Tipo = "Foto", Contenido = FotoPrueba });
        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        object proof(Guid rejection) => new { ResuelveOperacionId = rejection, FotoId = foto, Nombre = "Receptor sintético",
            Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow,
            ResultadosBultos = new[] { new { BultoId = parcel.Id, Version = load.VersionesBulto[parcel.Id], Entregado = true } } };
        Assert.Equal("Conflicto", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 5, "EntregaBultos", stop.EnvioId, 0, proof(Guid.NewGuid())))).Resultado);
        var resolution = Operacion(Guid.NewGuid(), 6, "EntregaBultos", stop.EnvioId, 0, proof(rejectedId));
        var accepted = await Aplicar(client, sheet, resolution); Assert.Equal("Aplicada", accepted.Resultado);
        Assert.Equal(JsonSerializer.Serialize(accepted), JsonSerializer.Serialize(await Aplicar(client, sheet, resolution)));
        var retained = await db.OperacionesJornada.AsNoTracking().SingleAsync(o => o.Id == rejectedId);
        Assert.Equal(original.Payload, retained.Payload); Assert.Equal(original.Respuesta, retained.Respuesta); Assert.Equal(original.Hash, retained.Hash);
        var delivered = await db.Bultos.AsNoTracking().SingleAsync(b => b.Id == parcel.Id);
        Assert.Equal(ResultadoBulto.Entregado, delivered.Resultado); Assert.Equal(EstadoCustodiaBulto.Destinatario, delivered.Custodia);
        Assert.Equal("Conflicto", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 7, "EntregaBultos", stop.EnvioId, 0, proof(rejectedId)))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 8, "FinJornada"))).Resultado);
        Assert.Equal("Conflicto", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 9, "EntregaBultos", stop.EnvioId, 0, proof(rejectedId)))).Resultado);
        Assert.Equal(delivered.Version, (await db.Bultos.AsNoTracking().SingleAsync(b => b.Id == parcel.Id)).Version);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public async Task C31_EntregaParcialRespetaPoliticaCustodiaPruebaReintentosYConflictoPorBulto(bool permitida, bool devolver, bool conforme)
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        await PublicarPoliticaParcial(data, permitida, devolver ? PoliticaPendientesParcial.DevolverComercio : PoliticaPendientesParcial.NuevaVisitaSinRecargo);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings(); settings["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var webhook = NexoRuta.IntegrationTests.Webhooks.WebhookAssay.Enabled && permitida && !devolver ? new NexoRuta.IntegrationTests.Webhooks.WebhookAssay() : null;
        webhook?.Configure(settings);
        var reloj = new ManualClock(DateTimeOffset.UtcNow);
        using var factory = new IntegrationApiFactory(runtime, settings: settings, timeProvider: reloj); if(webhook is not null) factory.UseKestrel(0); using var client = webhook is null ? factory.CreateHttpClient() : factory.CreateClient();
        var listo = await Preparar(data, client, bloquearDiscrepanciasCarga: false);
        await using (var beforeDb = data.CreateDbContext())
        {
            var shipment = await beforeDb.Envios.AsNoTracking().SingleAsync(e => e.Id == listo.EnvioId);
            var commerceAccessId = await beforeDb.AccesosUsuario.AsNoTracking()
                .Where(a => a.ComercioId == shipment.ComercioId).Select(a => a.Id).SingleAsync();
            using var commerceBeforeReturn = factory.CreateHttpClient();
            await commerceBeforeReturn.SetBearerForAccessAsync(data, commerceAccessId);
            var from = shipment.CreadoEnUtc.AddDays(-1).ToString("O");
            var to = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
            using var listBeforeReturn = await commerceBeforeReturn.GetAsync(
                $"/api/devoluciones?operadorId={shipment.OperadorId:D}&desdeUtc={Uri.EscapeDataString(from)}&hastaUtc={Uri.EscapeDataString(to)}&limite=50");
            Assert.Equal(HttpStatusCode.OK, listBeforeReturn.StatusCode);
            var pageBeforeReturn = await listBeforeReturn.Content.ReadFromJsonAsync<JsonElement>();
            Assert.DoesNotContain(pageBeforeReturn.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("envioId").GetGuid() == listo.EnvioId);
        }
        if(webhook is not null){await webhook.Subscribe(data,client);await client.SetBearerForAccessAsync(data,listo.AccesoId);}
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == listo.EnvioId);
            var otro = new Bulto(envio.OperadorId, envio.Id, "PARCIAL-DOS", 1000, 10, 10, 10); otro.RecibirEnDeposito(); db.Bultos.Add(otro); await db.SaveChangesAsync();
        }
        var hoja = await PlanYDespacho(client, listo);
        var p = hoja.Paradas.Single(); var primero = p.Bultos.First(); var segundo = p.Bultos.Last();
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 2, "CargarBulto", p.EnvioId, p.EnvioVersion, new { BultoId = primero.Id }))).Resultado);
        if (!permitida) Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 3, "CargarBulto", p.EnvioId, p.EnvioVersion, new { BultoId = segundo.Id }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), permitida ? 3 : 4, "ConfirmarCarga"))).Resultado);
        if (!permitida)
        {
            var r = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "EntregaBultos", p.EnvioId, p.EnvioVersion,
                new { ResultadosBultos = new[] { new { BultoId = primero.Id, Version = primero.Version + 1, Entregado = true, Motivo = (string?)null }, new { BultoId = segundo.Id, Version = segundo.Version + 1, Entregado = false, Motivo = (string?)"AUSENTE" } } }));
            Assert.Equal("Conflicto", r.Resultado); Assert.Contains("no admite", r.Detalle); return;
        }
        var foto = Guid.NewGuid();
        using var subida = await client.PutAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{foto}", new { p.EnvioId, Tipo = "Foto", Contenido = FotoPrueba });
        Assert.Equal(HttpStatusCode.NoContent, subida.StatusCode);
        var comando = Operacion(Guid.NewGuid(), 4, "EntregaBultos", p.EnvioId, 0, // aggregate may be stale: parcel versions are authoritative
            new { FotoId = foto, Nombre = "Receptor sintético", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow,
                ResultadosBultos = new[] { new { BultoId = primero.Id, Version = primero.Version + 1, Entregado = true, Motivo = (string?)null }, new { BultoId = segundo.Id, segundo.Version, Entregado = false, Motivo = (string?)"AUSENTE" } } });
        var resultado = await Aplicar(client, hoja, comando); Assert.Equal("Aplicada", resultado.Resultado);
        Assert.Equal(JsonSerializer.Serialize(resultado), JsonSerializer.Serialize(await Aplicar(client, hoja, comando)));
        await using var verificar = data.CreateDbContext();
        var actual = await verificar.Envios.SingleAsync(e => e.Id == p.EnvioId); Assert.Equal(EstadoEnvio.EntregadoParcialmente, actual.Estado);
        var bultos = await verificar.Bultos.Where(b => b.EnvioId == actual.Id).ToArrayAsync();
        Assert.Equal(ResultadoBulto.Entregado, bultos.Single(b => b.Id == primero.Id).Resultado);
        Assert.Equal(EstadoCustodiaBulto.Deposito, bultos.Single(b => b.Id == segundo.Id).Custodia);
        var deliveredBeforeTransfer = bultos.Single(b => b.Id == primero.Id);
        var prueba = Assert.Single(await verificar.PruebasEntrega.Where(e => e.EnvioId == actual.Id).ToArrayAsync()); Assert.Equal(primero.Id, prueba.BultoId); Assert.Equal(foto, prueba.FotoId);
        Assert.Equal("Conflicto", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "EntregaBultos", p.EnvioId, actual.Version,
            new { ResultadosBultos = new[] { new { BultoId = primero.Id, Version = primero.Version + 1, Entregado = true } } }))).Resultado);
        if (devolver)
        {
            var transferenciaCommand = new
            {
                ComandoId = Guid.NewGuid(), VersionEnvio = actual.Version, ConfirmarTransferenciaFisica = true,
                Bultos = new[] { new { BultoId = segundo.Id, VersionBulto = bultos.Single(b => b.Id == segundo.Id).Version } }
            };
            using var devuelta = await client.PostAsJsonAsync($"/api/devoluciones/envios/{actual.Id}/transferencias", transferenciaCommand);
            Assert.Equal(HttpStatusCode.OK, devuelta.StatusCode);
            var transferencia = (await devuelta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transferenciaId").GetGuid();
            var b = await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == segundo.Id);
            Assert.Equal(ResultadoBulto.Devuelto, b.Resultado); Assert.Equal(EstadoCustodiaBulto.Comercio, b.Custodia);
            var shipmentBeforeAck = await verificar.Envios.AsNoTracking().SingleAsync(shipment => shipment.Id == actual.Id);
            var outboxCountBeforeAck = await verificar.OutboxMessages.CountAsync();

            var commerceAccess = await verificar.AccesosUsuario.AsNoTracking().SingleAsync(a => a.ComercioId == actual.ComercioId);
            using var commerce = factory.CreateHttpClient();
            await commerce.SetBearerForAccessAsync(data, commerceAccess.Id);
            const string secondaryCommerceEmail = "ap04-concurrency-commerce@example.test";
            Guid secondaryCommerceAccessId;
            await using (var identity = data.CreateDbContext())
            {
                var commerceRoleId = commerceAccess.RolId
                    ?? throw new InvalidOperationException("The seeded commerce actor must have a role.");
                var commerceRole = await identity.Roles.SingleAsync(role => role.Id == commerceRoleId);
                var secondaryUser = new Usuario(secondaryCommerceEmail, AuthenticationTestData.Hash(secondaryCommerceEmail));
                var secondaryAccess = new AccesoUsuario(secondaryUser.Id, null, actual.ComercioId, commerceRole);
                identity.AddRange(secondaryUser, secondaryAccess);
                await identity.SaveChangesAsync();
                secondaryCommerceAccessId = secondaryAccess.Id;
            }
            using var secondApiFactory = new IntegrationApiFactory(runtime, settings: settings);
            using var commerceOnSecondApi = secondApiFactory.CreateHttpClient();
            await commerceOnSecondApi.SetBearerForAccessAsync(data, commerceAccess.Id);
            using var secondCommerceActor = secondApiFactory.CreateHttpClient();
            await secondCommerceActor.SetBearerForAccessAsync(data, secondaryCommerceAccessId);
            Guid foreignCommerceId;
            Guid foreignCommerceAccessId;
            await using (var foreignSetup = data.CreateDbContext())
            {
                await AuthenticationTestData.Seeder(foreignSetup, new AccesoInicial(
                    "ap04-foreign-commerce@example.test", "AP04 foreign operator", "AP04 foreign commerce",
                    "ap04-foreign-operator@example.test")).SeedAsync();
                var foreignAccessId = await (from access in foreignSetup.AccesosUsuario.AsNoTracking()
                    join user in foreignSetup.Usuarios.AsNoTracking() on access.UsuarioId equals user.Id
                    where access.ComercioId != null && user.Email == "ap04-foreign-commerce@example.test"
                    select access.Id).SingleAsync();
                var foreignAccess = await foreignSetup.AccesosUsuario.AsNoTracking()
                    .SingleAsync(access => access.Id == foreignAccessId);
                foreignCommerceId = foreignAccess.ComercioId!.Value;
                foreignCommerceAccessId = foreignAccess.Id;
                var permissionId = await foreignSetup.Permisos.AsNoTracking()
                    .Where(permission => permission.Codigo == "devoluciones.gestionar")
                    .Select(permission => permission.Id).SingleAsync();
                if (!await foreignSetup.RolesPermisos.AnyAsync(grant => grant.RolId == foreignAccess.RolId && grant.PermisoId == permissionId))
                {
                    foreignSetup.RolesPermisos.Add(new RolPermiso(foreignAccess.RolId!.Value, permissionId));
                    await foreignSetup.SaveChangesAsync();
                }
            }
            using var foreignCommerceActor = secondApiFactory.CreateHttpClient();
            await foreignCommerceActor.SetBearerForAccessAsync(data, foreignCommerceAccessId);
            var from = actual.CreadoEnUtc.AddDays(-1).ToString("O");
            var to = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
            using var list = await commerce.GetAsync($"/api/devoluciones?operadorId={actual.OperadorId:D}&desdeUtc={Uri.EscapeDataString(from)}&hastaUtc={Uri.EscapeDataString(to)}&limite=50");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var page = await list.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(page.GetProperty("items").EnumerateArray(), item => item.GetProperty("envioId").GetGuid() == actual.Id);
            using var detailResponse = await commerce.GetAsync($"/api/devoluciones/envios/{actual.Id:D}?operadorId={actual.OperadorId:D}");
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
            var returnedParcel = Assert.Single(detail.GetProperty("bultos").EnumerateArray(), item => item.GetProperty("id").GetGuid() == segundo.Id);
            Assert.True(returnedParcel.GetProperty("puedeAcusar").GetBoolean());
            using var foreignList = await foreignCommerceActor.GetAsync($"/api/devoluciones?operadorId={actual.OperadorId:D}&desdeUtc={Uri.EscapeDataString(from)}&hastaUtc={Uri.EscapeDataString(to)}&limite=50");
            Assert.Equal(HttpStatusCode.OK, foreignList.StatusCode);
            Assert.Empty((await foreignList.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
            using var foreignDetail = await foreignCommerceActor.GetAsync($"/api/devoluciones/envios/{actual.Id:D}?operadorId={actual.OperadorId:D}");
            Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode);
            using var foreignAck = await foreignCommerceActor.PostAsJsonAsync(
                $"/api/devoluciones/transferencias/{transferencia:D}/acuses?operadorId={actual.OperadorId:D}", new
                {
                    ComandoId = Guid.NewGuid(), VersionTransferencia = 1, Conforme = true,
                    Bultos = new[] { new { BultoId = segundo.Id, VersionBulto = returnedParcel.GetProperty("version").GetInt64(), Observacion = (string?)null } }
                });
            Assert.Equal(HttpStatusCode.NotFound, foreignAck.StatusCode);
            Assert.Equal(0, await verificar.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM public."AcusesDevolucionComercio"
                WHERE "ComercioId"={foreignCommerceId}
                """).SingleAsync());
            var ackCommand = new
            {
                ComandoId = Guid.NewGuid(), VersionTransferencia = 1, Conforme = conforme,
                Bultos = new[] { new { BultoId = segundo.Id, VersionBulto = returnedParcel.GetProperty("version").GetInt64(), Observacion = conforme ? null : "La caja llegó abierta." } }
            };
            await verificar.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM public."RolesPermisos" rp USING public."Permisos" p
                WHERE rp."PermisoId"=p."Id" AND p."Codigo"='devoluciones.gestionar'
                    AND rp."RolId"=(SELECT a."RolId" FROM public."AccesosUsuario" a WHERE a."Id"={commerceAccess.Id})
                """);
            using var readOnlyDetail = await commerce.GetAsync($"/api/devoluciones/envios/{actual.Id:D}?operadorId={actual.OperadorId:D}");
            Assert.Equal(HttpStatusCode.OK, readOnlyDetail.StatusCode);
            Assert.False(Assert.Single((await readOnlyDetail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bultos").EnumerateArray(), item => item.GetProperty("id").GetGuid() == segundo.Id).GetProperty("puedeAcusar").GetBoolean());
            using var deniedAck = await commerce.PostAsJsonAsync($"/api/devoluciones/transferencias/{transferencia:D}/acuses?operadorId={actual.OperadorId:D}", ackCommand);
            Assert.Equal(HttpStatusCode.Forbidden, deniedAck.StatusCode);
            await verificar.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."RolesPermisos" ("RolId","PermisoId")
                SELECT a."RolId",p."Id" FROM public."AccesosUsuario" a CROSS JOIN public."Permisos" p
                WHERE a."Id"={commerceAccess.Id} AND p."Codigo"='devoluciones.gestionar'
                ON CONFLICT DO NOTHING
                """);
            var concurrentResponses = await Task.WhenAll(
                commerce.PostAsJsonAsync($"/api/devoluciones/transferencias/{transferencia:D}/acuses?operadorId={actual.OperadorId:D}", ackCommand),
                commerceOnSecondApi.PostAsJsonAsync($"/api/devoluciones/transferencias/{transferencia:D}/acuses?operadorId={actual.OperadorId:D}", ackCommand));
            using var ack = concurrentResponses[0];
            using var ackFromSecondApi = concurrentResponses[1];
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
            Assert.Equal(HttpStatusCode.OK, ackFromSecondApi.StatusCode);
            var ackFirst = await ack.Content.ReadFromJsonAsync<JsonElement>();
            var ackSecond = await ackFromSecondApi.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(ackFirst.GetProperty("acuseId").GetGuid(), ackSecond.GetProperty("acuseId").GetGuid());
            Assert.Equal(conforme ? "Conforme" : "ConObservaciones", ackFirst.GetProperty("resultado").GetString());

            using var differentActorReplay = await secondCommerceActor.PostAsJsonAsync(
                $"/api/devoluciones/transferencias/{transferencia:D}/acuses?operadorId={actual.OperadorId:D}", ackCommand);
            Assert.Equal(HttpStatusCode.Conflict, differentActorReplay.StatusCode);

            await verificar.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM public."RolesPermisos" rp USING public."Permisos" p
                WHERE rp."PermisoId"=p."Id" AND p."Codigo"='devoluciones.gestionar'
                    AND rp."RolId"=(SELECT a."RolId" FROM public."AccesosUsuario" a WHERE a."Id"={commerceAccess.Id})
                """);
            using var revokedReplay = await commerceOnSecondApi.PostAsJsonAsync(
                $"/api/devoluciones/transferencias/{transferencia:D}/acuses?operadorId={actual.OperadorId:D}", ackCommand);
            Assert.Equal(HttpStatusCode.Forbidden, revokedReplay.StatusCode);
            await verificar.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."RolesPermisos" ("RolId","PermisoId")
                SELECT a."RolId",p."Id" FROM public."AccesosUsuario" a CROSS JOIN public."Permisos" p
                WHERE a."Id"={commerceAccess.Id} AND p."Codigo"='devoluciones.gestionar'
                ON CONFLICT DO NOTHING
                """);
            using var replay = await commerceOnSecondApi.PostAsJsonAsync(
                $"/api/devoluciones/transferencias/{transferencia:D}/acuses?operadorId={actual.OperadorId:D}", ackCommand);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(ackFirst.GetProperty("acuseId").GetGuid(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acuseId").GetGuid());
            var ackId = ackFirst.GetProperty("acuseId").GetGuid();
            var alteredAckCommand = new
            {
                ackCommand.ComandoId, ackCommand.VersionTransferencia, Conforme = !conforme,
                Bultos = new[] { new { BultoId = segundo.Id, VersionBulto = returnedParcel.GetProperty("version").GetInt64(), Observacion = conforme ? "La caja llegó abierta." : null } }
            };
            using var alteredReplay = await commerce.PostAsJsonAsync($"/api/devoluciones/transferencias/{transferencia:D}/acuses?operadorId={actual.OperadorId:D}", alteredAckCommand);
            Assert.Equal(HttpStatusCode.Conflict, alteredReplay.StatusCode);
            var afterAck = await verificar.Bultos.AsNoTracking().SingleAsync(parcel => parcel.Id == segundo.Id);
            Assert.Equal(b.Version, afterAck.Version);
            Assert.Equal(b.Resultado, afterAck.Resultado);
            Assert.Equal(b.Custodia, afterAck.Custodia);
            var afterAckShipment = await verificar.Envios.AsNoTracking().SingleAsync(shipment => shipment.Id == actual.Id);
            Assert.Equal(shipmentBeforeAck.Version, afterAckShipment.Version);
            Assert.Equal(shipmentBeforeAck.Estado, afterAckShipment.Estado);
            Assert.Equal(outboxCountBeforeAck, await verificar.OutboxMessages.CountAsync());
            using var readOnlyAck = await commerce.GetAsync($"/api/devoluciones/envios/{actual.Id:D}?operadorId={actual.OperadorId:D}");
            Assert.Equal(HttpStatusCode.OK, readOnlyAck.StatusCode);
            var acknowledgedParcel = Assert.Single((await readOnlyAck.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bultos").EnumerateArray(), item => item.GetProperty("id").GetGuid() == segundo.Id);
            Assert.Equal(conforme ? "Conforme" : "ConObservaciones", acknowledgedParcel.GetProperty("resultadoAcuse").GetString());
            Assert.Equal(conforme ? null : "La caja llegó abierta.", acknowledgedParcel.GetProperty("observacion").GetString());
            Assert.Equal(1, await verificar.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"AcusesDevolucionComercio\"").SingleAsync());
            var deliveredAfterAck = await verificar.Bultos.AsNoTracking().SingleAsync(parcel => parcel.Id == primero.Id);
            Assert.Equal(deliveredBeforeTransfer.Version, deliveredAfterAck.Version);
            Assert.Equal(deliveredBeforeTransfer.Resultado, deliveredAfterAck.Resultado);
            Assert.Equal(deliveredBeforeTransfer.Custodia, deliveredAfterAck.Custodia);
            using var finalDetail = await commerce.GetAsync($"/api/devoluciones/envios/{actual.Id:D}?operadorId={actual.OperadorId:D}");
            Assert.Equal(HttpStatusCode.OK, finalDetail.StatusCode);
            Assert.False(Assert.Single((await finalDetail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bultos").EnumerateArray(), item => item.GetProperty("id").GetGuid() == segundo.Id).GetProperty("puedeAcusar").GetBoolean());
            using var history = await commerce.GetAsync($"/api/devoluciones/envios/{actual.Id:D}/historial?operadorId={actual.OperadorId:D}");
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            var facts = (await history.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
            Assert.Contains(facts, fact => fact.GetProperty("tipo").GetString() == "Acuse del comercio"
                && fact.GetProperty("resultado").GetString() == (conforme ? "Conforme" : "ConObservaciones"));
            await using var immutableProbe = data.CreateDbContext();
            var immutableError = await Assert.ThrowsAsync<PostgresException>(() => immutableProbe.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE public.\"AcusesDevolucionComercio\" SET \"Resultado\"={(conforme ? "ConObservaciones" : "Conforme")} WHERE \"Id\"={ackId}"));
            Assert.Equal("55000", immutableError.SqlState);
        }
        else
        {
            using var nueva = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = listo.AccesoId, listo.VehiculoId,
                Fecha = FechaOperacion.AddDays(1), Paradas = new[] { new { p.EnvioId, LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1) } } });
            Assert.True(nueva.IsSuccessStatusCode, await nueva.Content.ReadAsStringAsync());
            var siguiente = (await nueva.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
            var visita = siguiente.Paradas.Single();
            Assert.Equal(segundo.Id, Assert.Single(visita.Bultos).Id);
            Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 6, "FinJornada"))).Resultado);
            var flota = await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota");
            var vehiculo = flota!.Single(v => v.Id == listo.VehiculoId); var ocupacion = vehiculo.Ocupaciones.Single(o => o.RutaId == hoja.Id);
            using var retorno = await client.PostAsJsonAsync($"/api/reparto/flota/{vehiculo.Id}/retorno", new { ComandoId = Guid.NewGuid(), OcupacionId = ocupacion.Id, ocupacion.Version, Motivo = "Retorno sintético tras entrega parcial." });
            Assert.True(retorno.IsSuccessStatusCode, await retorno.Content.ReadAsStringAsync());
            var vuelta = (await retorno.Content.ReadFromJsonAsync<ResultadoFlotaResponse>())!;
            using var liberar = await client.PostAsJsonAsync($"/api/reparto/flota/{vehiculo.Id}/liberar", new { ComandoId = Guid.NewGuid(), OcupacionId = ocupacion.Id, vuelta.Version, Motivo = "Inspección sintética sin carga." });
            Assert.True(liberar.IsSuccessStatusCode, await liberar.Content.ReadAsStringAsync());
            reloj.Set(LlegadaEstimada.AddDays(1));
            using var despacho = await client.PostAsJsonAsync($"/api/rutas/{siguiente.Id}/despacho", new { siguiente.Version });
            Assert.True(despacho.IsSuccessStatusCode, await despacho.Content.ReadAsStringAsync());
            Assert.Equal("Aplicada", (await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
            Assert.Equal("Aplicada", (await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 2, "CargarBulto", actual.Id, visita.EnvioVersion, new { BultoId = segundo.Id }))).Resultado);
            var salidaNueva = await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 3, "ConfirmarCarga")); Assert.Equal("Aplicada", salidaNueva.Resultado);
            var fotoIntento = Guid.NewGuid();
            using var subirIntento = await client.PutAsJsonAsync($"/api/mobile/jornadas/{siguiente.JornadaId}/evidencias/{fotoIntento}", new { EnvioId = actual.Id, Tipo = "Foto", Contenido = FotoPrueba });
            Assert.Equal(HttpStatusCode.NoContent, subirIntento.StatusCode);
            var intento = new { Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = actual.Id,
                VersionEnvio = salidaNueva.VersionesEnvio[actual.Id], OcurridaEn = LlegadaEstimada.AddDays(1).AddMinutes(2), Datos = new { Motivo = "AUSENTE", FotoId = fotoIntento } };
            var falloNuevo = await Aplicar(client, siguiente, intento); Assert.Equal("Aplicada", falloNuevo.Resultado);
            Assert.Equal(JsonSerializer.Serialize(falloNuevo), JsonSerializer.Serialize(await Aplicar(client, siguiente, intento)));
            Assert.Equal(EstadoEnvio.EntregadoParcialmente, (await verificar.Envios.AsNoTracking().SingleAsync(e => e.Id == actual.Id)).Estado);
            Assert.Equal(ResultadoBulto.Entregado, (await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == primero.Id)).Resultado);
            Assert.Equal(2, (await verificar.ParadasRuta.AsNoTracking().SingleAsync(p => p.RutaId == hoja.Id)).BultoIds.Length);
        }
        if(webhook is not null) await webhook.DeliverAndAssert(data,"partial","BultoActualizado","OperacionActualizada","NuevaVisita");
        var remitidos = await verificar.OutboxMessages.Where(m => m.EnvioId == actual.Id && m.VersionContrato == 2).ToArrayAsync(); Assert.NotEmpty(remitidos);
        // Durable worker ingress is idempotent and verifies the exact persisted event; commerce never reads another tenant's inbox.
        foreach (var aviso in remitidos)
            for (var i = 0; i < 2; i++) await verificar.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.receive_commerce_notice({aviso.Id},{aviso.Payload}::jsonb)");
        Assert.Equal(remitidos.Length, await verificar.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"AvisosComercio\" WHERE \"EnvioId\"={actual.Id}").SingleAsync());
    }
    private static async Task<HojaRutaResponse> PlanYDespacho(HttpClient client, Preparado p)
    {
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(p));
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        var h = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var salida = await client.PostAsJsonAsync($"/api/rutas/{h.Id}/despacho", new { h.Version });
        Assert.True(salida.IsSuccessStatusCode, await salida.Content.ReadAsStringAsync()); return (await salida.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
    }
    private static async Task PublicarPoliticaParcial(IntegrationDatabase data, bool permitida, PoliticaPendientesParcial politica)
    {
        await using var db = data.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var raiz = await db.ReglasOperativasOperadores.SingleAsync();
        var anterior = await db.VersionesReglasOperativas.Include(v => v.Motivos).Include(v => v.RequisitosPrueba).SingleAsync(v => v.Id == raiz.VersionVigenteId);
        var usuario = anterior.CreadaPorUsuarioId;
        var v = VersionReglasOperativas.CrearBorrador(raiz.OperadorId, usuario, anterior); db.Add(v); await db.SaveChangesAsync();
        v.ReemplazarConfiguracion(v.VersionConcurrencia, new(3, 1, PoliticaDevolucionAlAgotarIntentos.DecisionOperativa, 2880, 1440, 60,
            [TipoPruebaEntrega.Fotografia, TipoPruebaEntrega.NombreReceptor], [new("AUSENTE", "Ausente", true)]) { PermiteEntregaParcial = permitida, PoliticaPendientesParcial = politica });
        await db.SaveChangesAsync(); v.Publicar(v.VersionConcurrencia, raiz.UltimoNumeroPublicacion + 1, usuario); await db.SaveChangesAsync(); raiz.Publicar(v); await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
