using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Infrastructure.Persistence;

public sealed record RlsActorContext(
    string ActorKind,
    Guid? UserId = null,
    Guid? AccessId = null,
    Guid? OrganizationId = null,
    bool CanManageOrganizations = false,
    Guid? BootstrapOperatorId = null,
    Guid? BootstrapCommerceId = null,
    string? OrganizationType = null)
{
    public static RlsActorContext From(ContextoUsuario context) => new(
        context.TipoAcceso == TipoAccesoUsuario.Comercio ? "comercio" : "operador",
        context.UsuarioId, context.AccesoId,
        context.ComercioId ?? context.OperadorId,
        OrganizationType: context.Tipo);

    public static RlsActorContext From(ContextoPlataforma context) => new(
        "plataforma", context.UsuarioId, context.AccesoId,
        CanManageOrganizations: context.PuedeGestionarOrganizaciones);

    public static RlsActorContext From(IdentidadSinOrganizacion context) => new(
        "identidad", context.UsuarioId);

    public static RlsActorContext From(ContextoOrganizacionSuspendida context) => new(
        "suspendida", context.UsuarioId, context.AccesoId, context.OrganizacionId,
        OrganizationType: context.TipoOrganizacion.ToString());

    public static RlsActorContext Bootstrap(Guid? operatorId, Guid? commerceId) => new(
        "bootstrap", BootstrapOperatorId: operatorId, BootstrapCommerceId: commerceId);

    public static RlsActorContext Worker(Guid operatorId, Guid messageId, Guid shipmentId, Guid? processorId = null) => new(
        "worker", OrganizationId: operatorId)
    {
        JobMessageId = messageId,
        JobShipmentId = shipmentId,
        JobConsumer = "GenerarSeguimiento",
        ProcessorId = processorId
    };

    public Guid? SelectedOperatorId { get; init; }
    public Guid? ShipmentId { get; init; }
    public Guid? JobMessageId { get; init; }
    public Guid? JobShipmentId { get; init; }
    public Guid? ProcessorId { get; init; }
    public Guid? SelectedOrganizationId { get; init; }
    public Guid? TargetUserId { get; init; }
    public Guid? TargetRoleId { get; init; }
    public string? JobConsumer { get; init; }
}

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
        if ((context.ActorKind is "comercio" or "operador" or "suspendida") && context.OrganizationId is null)
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
            ("nexoruta.selected_operator_id", operatorId.ToString("D")),
            ("nexoruta.shipment_id", shipmentId.ToString("D")),
            ("nexoruta.recipient_id", recipientId.ToString("D")),
            ("nexoruta.address_id", addressId.ToString("D")),
            ("nexoruta.parcel_id", parcelId.ToString("D")));
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
            ("nexoruta.selected_org_id", organizationId.ToString("D")),
            ("nexoruta.target_user_id", userId.ToString("D")),
            ("nexoruta.target_role_id", roleId.ToString("D")));
    }

    public static Task SetBootstrapUserAsync(
        this NexoRutaDbContext db,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Bootstrap user context requires a valid id and an active transaction.");
        return ExecuteSettingsAsync(db, null, cancellationToken,
            ("nexoruta.user_id", userId.ToString("D")));
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
            settings.AddRange(
            [
                ("nexoruta.actor_kind", context.ActorKind),
                ("nexoruta.user_id", Value(context.UserId)),
                ("nexoruta.access_id", Value(context.AccessId)),
                ("nexoruta.org_id", Value(context.OrganizationId)),
                ("nexoruta.platform_manage_organizations", context.CanManageOrganizations ? "true" : "false"),
                ("nexoruta.bootstrap_operator_id", Value(context.BootstrapOperatorId)),
                ("nexoruta.bootstrap_commerce_id", Value(context.BootstrapCommerceId)),
                ("nexoruta.target_org_type", context.OrganizationType ?? ""),
                ("nexoruta.job_message_id", Value(context.JobMessageId)),
                ("nexoruta.job_consumer", context.JobConsumer ?? ""),
                ("nexoruta.job_shipment_id", Value(context.JobShipmentId)),
                ("nexoruta.processor_id", Value(context.ProcessorId)),
                ("nexoruta.selected_operator_id", Value(context.SelectedOperatorId)),
                ("nexoruta.shipment_id", Value(context.ShipmentId)),
                ("nexoruta.selected_org_id", Value(context.SelectedOrganizationId)),
                ("nexoruta.target_user_id", Value(context.TargetUserId)),
                ("nexoruta.target_role_id", Value(context.TargetRoleId)),
                ("nexoruta.auth_access_id", ""),
                ("nexoruta.auth_user_id", ""),
                ("nexoruta.auth_org_id", ""),
                ("nexoruta.auth_role_id", "")
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
