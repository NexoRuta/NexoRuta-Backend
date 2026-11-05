using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using System.Text.Json;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Domain.Administracion;
using Npgsql;
using System.Data;
using Microsoft.EntityFrameworkCore.Storage;
using NexoRuta.Application.Envios.Importacion;

namespace NexoRuta.Infrastructure.Envios.Repositories;

public sealed class EfEnviosRepository(NexoRutaDbContext db) : IEnviosRepository
{
    public async Task BloquearSolicitudRetiroAsync(Guid comercioId, Guid solicitudId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("La solicitud requiere una transacción de autorización.");
        var clave = $"retiro:{comercioId:N}:{solicitudId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({clave},0))", ct);
    }

    public async Task<IEnviosImportTransaction> IniciarImportacionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await db.BeginOrJoinAsync(cancellationToken);
        try
        {
            if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
                throw new InvalidOperationException("CSV import requires a READ COMMITTED transaction for post-lock identity reads.");
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public async Task BloquearReferenciasImportacionAsync(
        IReadOnlyCollection<string> referencias,
        CancellationToken cancellationToken = default)
    {
        ExigirTransaccionImportacion();
        if (referencias.Count == 0)
            return;
        await db.Database.ExecuteSqlRawAsync(
            $"SELECT {RlsSqlFunctionContract.LockImportReferences}({{0}}::text[])",
            [(object)referencias.Distinct(StringComparer.Ordinal).ToArray()], cancellationToken);
    }

    public async Task<IReadOnlyList<IdentidadEnvioImportadoEncontrada>> BuscarIdentidadesImportacionAsync(
        IReadOnlyCollection<string> referencias,
        CancellationToken cancellationToken = default)
    {
        ExigirTransaccionImportacion();
        if (referencias.Count == 0)
            return [];
        return await db.Database.SqlQueryRaw<IdentidadEnvioImportadoEncontrada>(
                "SELECT * FROM " + RlsSqlFunctionContract.FindImportReferences + "({0}::text[])",
                (object)referencias.Distinct(StringComparer.Ordinal).ToArray())
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid?> ObtenerVersionReglasParaAdmisionAsync(Guid operadorId, CancellationToken cancellationToken = default)
    {
        try { return await db.Database.SqlQuery<Guid?>($"SELECT nexoruta_app.shipment_rules_for_admission({operadorId}) AS \"Value\"").SingleAsync(cancellationToken); }
        catch (PostgresException error) when (error.SqlState == "NXR01")
        { throw new OrganizacionSuspendidaException("No se puede admitir el envío mientras el operador seleccionado no esté disponible."); }
    }
    public async Task<NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa> GuardarAsync(
        Envio envio,
        Destinatario destinatario,
        Direccion direccion,
        IReadOnlyList<Bulto> bultos,
        NexoRuta.Application.Envios.Commands.ServicioEnvioDeclarado servicio,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        await VerificarOrganizacionesParaAltaAsync(envio.ComercioId, envio.OperadorId, cancellationToken);
        var cargo = await TarifasAdmision.CotizarAsync(db, envio.OperadorId, servicio, bultos, cancellationToken);
        await CrearEnBaseAsync(envio, destinatario, direccion, bultos, null, cancellationToken);
        await TarifasAdmision.GuardarAsync(db, envio, servicio, cargo, cancellationToken);
        await ConfirmarDestinoInicialAsync(envio, servicio, cancellationToken);
        if (envio.EsRetiro)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.configure_pickup({envio.Id},{envio.SolicitudRetiroId},{envio.MotivoRetiro},{envio.FranjaRetiroDesdeUtc},{envio.FranjaRetiroHastaUtc},{envio.SolicitudRetiroHuella})", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return cargo;
    }

    public async Task<IReadOnlyList<NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa>> GuardarImportadosAsync(
        IReadOnlyList<EnvioImportadoParaPersistir> envios,
        CancellationToken cancellationToken = default)
    {
        if (envios.Count == 0)
            return [];
        var commerceId = envios[0].Envio.ComercioId;
        var operatorId = envios[0].Envio.OperadorId;
        if (envios.Any(x => x.Envio.ComercioId != commerceId || x.Envio.OperadorId != operatorId))
            throw new ArgumentException("An import batch must use one commerce and one operator.", nameof(envios));

        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        await VerificarOrganizacionesParaAltaAsync(commerceId, operatorId, cancellationToken);
        var cargos = new List<NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa>();
        // Resolve every destination before writing any row, retaining the publication/zone locks.
        foreach (var item in envios)
            cargos.Add(await TarifasAdmision.CotizarAsync(db, item.Envio.OperadorId, item.Servicio, item.Bultos, cancellationToken));
        for (var index = 0; index < envios.Count; index++)
        {
            var item = envios[index];
            var cargo = cargos[index];
            await CrearEnBaseAsync(item.Envio, item.Destinatario, item.Direccion, item.Bultos, item, cancellationToken);
            await TarifasAdmision.GuardarAsync(db, item.Envio, item.Servicio, cargo, cancellationToken);
            await ConfirmarDestinoInicialAsync(item.Envio, item.Servicio, cancellationToken);
            // The RLS INSERT policy scopes each outbox pair to the currently selected shipment.
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return cargos;
    }

    private async Task ConfirmarDestinoInicialAsync(Envio envio,
        NexoRuta.Application.Envios.Commands.ServicioEnvioDeclarado servicio, CancellationToken ct)
    {
        var confirmado = await db.Database.SqlQuery<bool>($"SELECT nexoruta_app.confirm_shipment_destination({envio.Id},0::bigint,{servicio.Latitud},{servicio.Longitud},NULL::text) AS \"Value\"").SingleAsync(ct);
        if (!confirmado) throw new InvalidOperationException("El destino inicial no pudo confirmarse de forma atómica.");
    }

    private async Task VerificarOrganizacionesParaAltaAsync(Guid commerceId, Guid operatorId, CancellationToken cancellationToken)
    {
        // Keep the commerce lock on the API path; create_shipment locks the chosen operator under its scoped function role.
        var commerce = await db.Comercios.FromSqlInterpolated(
            $"SELECT * FROM \"Comercios\" WHERE \"Id\" = {commerceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var operatorEntity = await db.Operadores.SingleOrDefaultAsync(x => x.Id == operatorId, cancellationToken);
        if (commerce is null || operatorEntity is null)
            throw new OrganizacionSuspendidaException("No se puede crear el envío porque una organización ya no está disponible.");
        if (commerce.Estado != EstadoOrganizacion.Activa || operatorEntity.Estado != EstadoOrganizacion.Activa)
            throw new OrganizacionSuspendidaException("No se puede crear el envío mientras una organización esté suspendida.");
    }

    private async Task CrearEnBaseAsync(
        Envio envio,
        Destinatario destinatario,
        Direccion direccion,
        IReadOnlyList<Bulto> bultos,
        EnvioImportadoParaPersistir? identidad,
        CancellationToken cancellationToken)
    {
        if (destinatario.OperadorId != envio.OperadorId || direccion.OperadorId != envio.OperadorId ||
            bultos.Count == 0 || bultos.Any(b => b.OperadorId != envio.OperadorId || b.EnvioId != envio.Id))
            throw new ArgumentException("Shipment aggregate tenant keys must refer to the same shipment and operator.");

        var bulto = bultos[0];
        await db.SetShipmentHistoryContextAsync(
            identidad is null ? "AltaIndividual" : "Importacion",
            envio.Id,
            cancellationToken: cancellationToken);
        await db.SetShipmentAsync(envio.OperadorId, envio.Id, destinatario.Id, direccion.Id, bulto.Id, cancellationToken);
        Guid createdId;
        try
        {
            createdId = await db.Database.SqlQueryRaw<Guid>(
                "SELECT " + RlsSqlFunctionContract.CreateShipment +
                "({0}, {1}, {2}, {3}, {4}, {5}, {6}::jsonb, {7}) AS \"Value\"",
                envio.Id, destinatario.Id, direccion.Id,
                destinatario.Nombre, destinatario.Email, direccion.Descripcion,
                JsonSerializer.Serialize(bultos), envio.VersionReglasId)
                .SingleAsync(cancellationToken);
        }
        catch (PostgresException error) when (error.SqlState == "NXR01")
        {
            throw new OrganizacionSuspendidaException("No se puede crear el envío mientras el operador seleccionado está suspendido.");
        }
        catch (PostgresException error) when (error.SqlState == "NXR03")
        { throw new NexoRuta.Application.Reparto.Exceptions.ConflictoRepartoException("Las reglas del operador cambiaron. Volvé a crear el envío para fijar la publicación vigente."); }
        if (createdId != envio.Id)
            throw new InvalidOperationException("The database did not create the requested shipment.");

        if (identidad is not null)
        {
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT {RlsSqlFunctionContract.BindImportIdentity}({{0}}, {{1}}, {{2}}, {{3}}, {{4}})",
                [identidad.ReferenciaExterna, envio.Id, envio.OperadorId, identidad.VersionHuella, identidad.Huella],
                cancellationToken);
        }


        var trackingRequested = OutboxMessage.ForShipment(
            operatorId: envio.OperadorId,
            shipmentId: envio.Id,
            type: OutboxMessageTypes.TrackingRequested,
            destination: OutboxDestino.RabbitMq,
            payload: JsonSerializer.Serialize(new { envioId = envio.Id, operadorId = envio.OperadorId }));
        var shipmentCreated = OutboxMessage.ForShipment(
            operatorId: envio.OperadorId,
            shipmentId: envio.Id,
            type: OutboxMessageTypes.ShipmentCreated,
            destination: OutboxDestino.Kafka,
            payload: JsonSerializer.Serialize(new
            {
                envioId = envio.Id,
                operadorId = envio.OperadorId,
                comercioId = envio.ComercioId,
                estado = envio.Estado.ToString()
            }));
        db.AddRange(trackingRequested, shipmentCreated);
    }

    private void ExigirTransaccionImportacion()
    {
        if (db.Database.CurrentTransaction is null
            || db.Database.CurrentTransaction.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Import lock and lookup require the request READ COMMITTED transaction.");
    }

    public async Task<IReadOnlyList<EnvioDetalle>> ListarAsync(
        Guid? operadorId,
        Guid? comercioId,
        CancellationToken cancellationToken = default)
    {
        var shipments = await (
            from envio in db.Envios
            join comercio in db.Comercios on envio.ComercioId equals comercio.Id
            join selectedOperator in db.Operadores on envio.OperadorId equals selectedOperator.Id
            join usuario in db.Usuarios on envio.CreadoPorUsuarioId equals usuario.Id
            join destinatario in db.Destinatarios on envio.DestinatarioId equals destinatario.Id
            join direccion in db.Direcciones on envio.DireccionId equals direccion.Id
            where (operadorId != null && envio.OperadorId == operadorId)
                || (comercioId != null && envio.ComercioId == comercioId)
            orderby envio.CreadoEnUtc descending, envio.Id descending
            select new
            {
                envio.Id,
                envio.OperadorId,
                envio.ComercioId,
                envio.CreadoPorUsuarioId,
                UsuarioEmail = usuario.Email,
                OperadorNombre = selectedOperator.Nombre,
                ComercioNombre = comercio.Nombre,
                DestinatarioNombre = destinatario.Nombre,
                DestinatarioEmail = destinatario.Email,
                Direccion = direccion.Descripcion,
                DestinoConfirmado = direccion.ConfirmadaEnUtc != null,
                Estado = envio.Estado.ToString(),
                envio.Version,
                envio.EsRetiro, envio.MotivoRetiro, envio.FranjaRetiroDesdeUtc, envio.FranjaRetiroHastaUtc, envio.SolicitudRetiroId, envio.SolicitudRetiroHuella,
                envio.CreadoEnUtc
            }).ToListAsync(cancellationToken);

        if (shipments.Count == 0)
            return [];

        var ids = shipments.Select(x => x.Id).ToArray();
        var cargos = await db.Database.SqlQuery<CargoListado>($"SELECT DISTINCT ON (\"EnvioId\") \"EnvioId\",\"Resultado\"::text AS \"Resultado\" FROM public.\"TarifasEnvio\" WHERE \"EnvioId\" = ANY({ids}) ORDER BY \"EnvioId\",\"CreadoEnUtc\" DESC,\"Id\" DESC").ToArrayAsync(cancellationToken);
        var tarifas = cargos.ToDictionary(c => c.EnvioId, c => JsonSerializer.Deserialize<NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa>(c.Resultado, TarifasAdmision.Json));
        var referencesByShipment = await ListarReferenciasExternasAsync(ids, comercioId, cancellationToken);
        var packages = await db.Bultos
            .Where(x => ids.Contains(x.EnvioId))
            .Select(x => new
            {
                x.EnvioId,
                Detail = new BultoDetalle(
                Id: x.Id, Codigo: x.Codigo, PesoGramos: x.PesoGramos, LargoCentimetros: x.LargoCentimetros,
                AnchoCentimetros: x.AnchoCentimetros, AltoCentimetros: x.AltoCentimetros)
                { Custodia = x.Custodia.ToString(), Resultado = x.Resultado.ToString(), MotivoNoEntrega = x.MotivoNoEntrega }
            })
            .ToListAsync(cancellationToken);
        var packagesByShipment = packages.ToLookup(x => x.EnvioId, x => x.Detail);

              return shipments.Select(x => new EnvioDetalle(
            Id: x.Id,
            Origen: new OrigenEnvio(
                OperadorId: x.OperadorId,
                ComercioId: x.ComercioId,
                CreadoPorUsuarioId: x.CreadoPorUsuarioId,
                UsuarioEmail: x.UsuarioEmail,
                OperadorNombre: x.OperadorNombre,
                ComercioNombre: x.ComercioNombre),
            DestinatarioNombre: x.DestinatarioNombre,
            Direccion: x.Direccion,
            Estado: x.Estado,
            Version: x.Version,
            Bultos: packagesByShipment[x.Id].ToArray(),
            CreadoEnUtc: x.CreadoEnUtc)
        {
            Tarifa = tarifas.GetValueOrDefault(x.Id),
            EsRetiro = x.EsRetiro, MotivoRetiro = x.MotivoRetiro, FranjaRetiroDesdeUtc = x.FranjaRetiroDesdeUtc,
            FranjaRetiroHastaUtc = x.FranjaRetiroHastaUtc, SolicitudRetiroId = x.SolicitudRetiroId, SolicitudRetiroHuella = x.SolicitudRetiroHuella,
            DestinoConfirmado = x.DestinoConfirmado,
            ReferenciaExterna = referencesByShipment.GetValueOrDefault(x.Id),
            DestinatarioEmail = x.DestinatarioEmail
        }).ToArray();
    }

    private sealed class CargoListado
    {
        public Guid EnvioId { get; set; }
        public string Resultado { get; set; } = "";
    }

    private async Task<Dictionary<Guid, string>> ListarReferenciasExternasAsync(
        Guid[] shipmentIds,
        Guid? commerceId,
        CancellationToken cancellationToken)
    {
        var references = new Dictionary<Guid, string>();
        if (commerceId is null || shipmentIds.Length == 0)
            return references;

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"EnvioId\", \"ReferenciaExterna\" " +
                "FROM nexoruta_app.list_import_shipment_references(@shipment_ids)";
            command.Transaction =
                db.Database.CurrentTransaction?.GetDbTransaction();

            var parameter = command.CreateParameter();
            parameter.ParameterName = "shipment_ids";
            parameter.Value = shipmentIds;
            command.Parameters.Add(parameter);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
                references.Add(reader.GetGuid(0), reader.GetString(1));
        }
        finally
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
        }

        return references;
    }
}
