using NexoRuta.Domain.Administracion;

namespace NexoRuta.Infrastructure.Persistence;

public enum RlsActorKind
{
    Comercio,
    Operador,
    Plataforma,
    Identidad,
    Suspendida,
    Bootstrap,
    Worker
}

public static class RlsContextContract
{
    public const string ActorKind = "nexoruta.actor_kind";
    public const string UserId = "nexoruta.user_id";
    public const string AccessId = "nexoruta.access_id";
    public const string OrganizationId = "nexoruta.org_id";
    public const string CanManageOrganizations = "nexoruta.platform_manage_organizations";
    public const string BootstrapOperatorId = "nexoruta.bootstrap_operator_id";
    public const string BootstrapCommerceId = "nexoruta.bootstrap_commerce_id";
    public const string OrganizationType = "nexoruta.target_org_type";
    public const string JobMessageId = "nexoruta.job_message_id";
    public const string JobConsumer = "nexoruta.job_consumer";
    public const string JobShipmentId = "nexoruta.job_shipment_id";
    public const string ProcessorId = "nexoruta.processor_id";
    public const string SelectedOperatorId = "nexoruta.selected_operator_id";
    public const string ShipmentId = "nexoruta.shipment_id";
    public const string RecipientId = "nexoruta.recipient_id";
    public const string AddressId = "nexoruta.address_id";
    public const string ParcelId = "nexoruta.parcel_id";
    public const string SelectedOrganizationId = "nexoruta.selected_org_id";
    public const string TargetUserId = "nexoruta.target_user_id";
    public const string TargetRoleId = "nexoruta.target_role_id";
    public const string AuthAccessId = "nexoruta.auth_access_id";
    public const string AuthUserId = "nexoruta.auth_user_id";
    public const string AuthOrganizationId = "nexoruta.auth_org_id";
    public const string AuthRoleId = "nexoruta.auth_role_id";
    public const string HistorySourceType = "nexoruta.history_source_type";
    public const string HistorySourceId = "nexoruta.history_source_id";
    public const string HistoryOperationId = "nexoruta.history_operation_id";
    public const string HistoryCorrelationId = "nexoruta.history_correlation_id";
    public const string HistoryOccurredAt = "nexoruta.history_occurred_at";
    public const string HistoryFactCode = "nexoruta.history_fact_code";
    public const string HistoryProcess = "nexoruta.history_process";

    public static string ActorSqlValue(RlsActorKind actorKind) => actorKind switch
    {
        RlsActorKind.Comercio => "comercio",
        RlsActorKind.Operador => "operador",
        RlsActorKind.Plataforma => "plataforma",
        RlsActorKind.Identidad => "identidad",
        RlsActorKind.Suspendida => "suspendida",
        RlsActorKind.Bootstrap => "bootstrap",
        RlsActorKind.Worker => "worker",
        _ => throw new ArgumentOutOfRangeException(nameof(actorKind), actorKind, "Unknown RLS actor kind.")
    };

    public static string OrganizationSqlValue(TipoAccesoUsuario organizationType) => organizationType switch
    {
        TipoAccesoUsuario.Comercio => "Comercio",
        TipoAccesoUsuario.Operador => "Operador",
        _ => throw new ArgumentOutOfRangeException(nameof(organizationType), organizationType, "Unknown organization kind.")
    };
}
