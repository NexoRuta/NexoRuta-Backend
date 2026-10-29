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
    public async Task GuardarAsync(
        Envio envio,
        Destinatario destinatario,
        Direccion direccion,
        Bulto bulto,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        await VerificarOrganizacionesParaAltaAsync(envio.ComercioId, envio.OperadorId, cancellationToken);
        await CrearEnBaseAsync(envio, destinatario, direccion, bulto, null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task GuardarImportadosAsync(
        IReadOnlyList<EnvioImportadoParaPersistir> envios,
        CancellationToken cancellationToken = default)
    {
        if (envios.Count == 0)
            return;
        var commerceId = envios[0].Envio.ComercioId;
        var operatorId = envios[0].Envio.OperadorId;
        if (envios.Any(x => x.Envio.ComercioId != commerceId || x.Envio.OperadorId != operatorId))
            throw new ArgumentException("An import batch must use one commerce and one operator.", nameof(envios));

        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        await VerificarOrganizacionesParaAltaAsync(commerceId, operatorId, cancellationToken);
        foreach (var item in envios)
        {
            await CrearEnBaseAsync(item.Envio, item.Destinatario, item.Direccion, item.Bulto, item, cancellationToken);
            // The RLS INSERT policy scopes each outbox pair to the currently selected shipment.
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
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
        Bulto bulto,
        EnvioImportadoParaPersistir? identidad,
        CancellationToken cancellationToken)
    {
        if (destinatario.OperadorId != envio.OperadorId || direccion.OperadorId != envio.OperadorId ||
            bulto.OperadorId != envio.OperadorId || bulto.EnvioId != envio.Id)
            throw new ArgumentException("Shipment aggregate tenant keys must refer to the same shipment and operator.");

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
                "({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}, {12}) AS \"Value\"",
                envio.Id, destinatario.Id, direccion.Id, bulto.Id,
                destinatario.Nombre, destinatario.Email, direccion.Descripcion, bulto.Codigo,
                bulto.PesoGramos, bulto.LargoCentimetros, bulto.AnchoCentimetros, bulto.AltoCentimetros, envio.VersionReglasId)
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
            orderby envio.Id descending
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
                Direccion = direccion.Descripcion,
                DestinoConfirmado = direccion.ConfirmadaEnUtc != null,
                Estado = envio.Estado.ToString(),
                envio.Version
            }).ToListAsync(cancellationToken);

        if (shipments.Count == 0)
            return [];

        var ids = shipments.Select(x => x.Id).ToArray();
        var packages = await db.Bultos
            .Where(x => ids.Contains(x.EnvioId))
            .Select(x => new
            {
                x.EnvioId,
                Detail = new BultoDetalle(
                Codigo: x.Codigo, PesoGramos: x.PesoGramos, LargoCentimetros: x.LargoCentimetros,
                AnchoCentimetros: x.AnchoCentimetros, AltoCentimetros: x.AltoCentimetros)
                { Custodia = x.Custodia.ToString() }
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
            Bultos: packagesByShipment[x.Id].ToArray())
        { DestinoConfirmado = x.DestinoConfirmado }).ToArray();
    }
}
