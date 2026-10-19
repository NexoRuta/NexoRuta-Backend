using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Infrastructure.Persistence;

public record RlsActorContext(
    RlsActorKind ActorKind,
    Guid? UserId = null,
    Guid? AccessId = null,
    Guid? OrganizationId = null,
    bool CanManageOrganizations = false,
    TipoAccesoUsuario? OrganizationType = null)
{
    public static RlsActorContext From(ContextoUsuario context) => new(
        context.TipoAcceso switch
        {
            TipoAccesoUsuario.Comercio => RlsActorKind.Comercio,
            TipoAccesoUsuario.Operador => RlsActorKind.Operador,
            _ => throw new ArgumentOutOfRangeException(nameof(context), "Unknown access kind.")
        },
        context.UsuarioId, context.AccesoId,
        context.ComercioId ?? context.OperadorId,
        OrganizationType: context.TipoAcceso);

    public static RlsActorContext From(ContextoPlataforma context) => new(
        RlsActorKind.Plataforma, context.UsuarioId, context.AccesoId,
        CanManageOrganizations: context.PuedeGestionarOrganizaciones);

    public static RlsActorContext From(IdentidadSinOrganizacion context) => new(
        RlsActorKind.Identidad, context.UsuarioId);

    public static RlsActorContext From(ContextoOrganizacionSuspendida context)
    {
        if (!Enum.IsDefined(context.TipoOrganizacion))
            throw new ArgumentOutOfRangeException(nameof(context), "Unknown organization kind.");
        return new(RlsActorKind.Suspendida, context.UsuarioId, context.AccesoId, context.OrganizacionId,
            OrganizationType: context.TipoOrganizacion);
    }

    public static RlsBootstrapContext Bootstrap(Guid? operatorId, Guid? commerceId) => new(operatorId, commerceId);

    public static RlsWorkerContext Worker(Guid operatorId, Guid messageId, Guid shipmentId, Guid? processorId = null)
        => new(operatorId, messageId, shipmentId, processorId);
}

public sealed record RlsBootstrapContext(Guid? OperatorId, Guid? CommerceId)
    : RlsActorContext(RlsActorKind.Bootstrap);

public sealed record RlsWorkerContext(Guid OperatorId, Guid MessageId, Guid ShipmentId, Guid? ProcessorId)
    : RlsActorContext(RlsActorKind.Worker, OrganizationId: OperatorId);

public static class RlsTransaction
{
    public static async Task<RlsTransactionLease> BeginOrJoinAsync(
        this NexoRutaDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is not null)
            return new RlsTransactionLease(null);
        return new RlsTransactionLease(await db.Database.BeginTransactionAsync(cancellationToken));
    }

    public static Task ApplyAsync(
        this NexoRutaDbContext db,
        RlsActorContext context,
        CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("RLS context must be set inside an active database transaction.");
        if ((context.ActorKind is RlsActorKind.Comercio or RlsActorKind.Operador or RlsActorKind.Suspendida) && context.OrganizationId is null)
            throw new ArgumentException("An organization context requires a persisted organization id.", nameof(context));

        return ExecuteSettingsAsync(db, context, cancellationToken);
    }

    public static Task SetShipmentAsync(
        this NexoRutaDbContext db,
        Guid operatorId,
        Guid shipmentId,
        Guid recipientId,
        Guid addressId,
        Guid parcelId,
        CancellationToken cancellationToken = default)
    {
        if (operatorId == Guid.Empty || shipmentId == Guid.Empty || recipientId == Guid.Empty ||
            addressId == Guid.Empty || parcelId == Guid.Empty || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Shipment context requires valid ids and an active transaction.");
        return ExecuteSettingsAsync(db, null, cancellationToken,
            (RlsContextContract.SelectedOperatorId, operatorId.ToString("D")),
            (RlsContextContract.ShipmentId, shipmentId.ToString("D")),
            (RlsContextContract.RecipientId, recipientId.ToString("D")),
            (RlsContextContract.AddressId, addressId.ToString("D")),
            (RlsContextContract.ParcelId, parcelId.ToString("D")));
    }

    public static Task SetOrganizationCreationAsync(
        this NexoRutaDbContext db,
        Guid organizationId,
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || userId == Guid.Empty || roleId == Guid.Empty ||
            db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Organization creation context requires valid ids and an active transaction.");
        return ExecuteSettingsAsync(db, null, cancellationToken,
            (RlsContextContract.SelectedOrganizationId, organizationId.ToString("D")),
            (RlsContextContract.TargetUserId, userId.ToString("D")),
            (RlsContextContract.TargetRoleId, roleId.ToString("D")));
    }

    public static Task SetBootstrapUserAsync(
        this NexoRutaDbContext db,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Bootstrap user context requires a valid id and an active transaction.");
        return ExecuteSettingsAsync(db, null, cancellationToken,
            (RlsContextContract.UserId, userId.ToString("D")));
    }

    private static async Task ExecuteSettingsAsync(
        NexoRutaDbContext db,
        RlsActorContext? context,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] extra)
    {
        var settings = new List<(string Name, string Value)>();
        if (context is not null)
        {
            var bootstrap = context as RlsBootstrapContext;
            var worker = context as RlsWorkerContext;
            settings.AddRange(
            [
                (RlsContextContract.ActorKind, RlsContextContract.ActorSqlValue(context.ActorKind)),
                (RlsContextContract.UserId, Value(context.UserId)),
                (RlsContextContract.AccessId, Value(context.AccessId)),
                (RlsContextContract.OrganizationId, Value(context.OrganizationId)),
                (RlsContextContract.CanManageOrganizations, context.CanManageOrganizations ? "true" : "false"),
                (RlsContextContract.BootstrapOperatorId, Value(bootstrap?.OperatorId)),
                (RlsContextContract.BootstrapCommerceId, Value(bootstrap?.CommerceId)),
                (RlsContextContract.OrganizationType, context.OrganizationType is { } organizationType ? RlsContextContract.OrganizationSqlValue(organizationType) : ""),
                (RlsContextContract.JobMessageId, Value(worker?.MessageId)),
                (RlsContextContract.JobConsumer, worker is not null ? OutboxMessageTypes.TrackingRequested : ""),
                (RlsContextContract.JobShipmentId, Value(worker?.ShipmentId)),
                (RlsContextContract.ProcessorId, Value(worker?.ProcessorId)),
                (RlsContextContract.SelectedOperatorId, ""),
                (RlsContextContract.ShipmentId, ""),
                (RlsContextContract.SelectedOrganizationId, ""),
                (RlsContextContract.TargetUserId, ""),
                (RlsContextContract.TargetRoleId, ""),
                (RlsContextContract.AuthAccessId, ""),
                (RlsContextContract.AuthUserId, ""),
                (RlsContextContract.AuthOrganizationId, ""),
                (RlsContextContract.AuthRoleId, "")
            ]);
        }
        settings.AddRange(extra);

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "SELECT set_config(@name, @value, true)";
            var nameParameter = command.CreateParameter();
            nameParameter.ParameterName = "name";
            nameParameter.DbType = DbType.String;
            command.Parameters.Add(nameParameter);
            var valueParameter = command.CreateParameter();
            valueParameter.ParameterName = "value";
            valueParameter.DbType = DbType.String;
            command.Parameters.Add(valueParameter);

            foreach (var (name, value) in settings)
            {
                nameParameter.Value = name;
                valueParameter.Value = value;
                await command.ExecuteScalarAsync(cancellationToken);
            }
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }

        static string Value(Guid? value) => value?.ToString("D") ?? "";
    }
}

public sealed class RlsTransactionLease(IDbContextTransaction? ownedTransaction) : IAsyncDisposable
{
    public Task CommitAsync(CancellationToken cancellationToken = default)
        => ownedTransaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (ownedTransaction is not null)
            await ownedTransaction.DisposeAsync();
    }
}
