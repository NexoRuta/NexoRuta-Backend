namespace NexoRuta.Infrastructure.Persistence;

public static class RlsSqlFunctionContract
{
    public const string CancelShipment = "nexoruta_app.cancel_shipment";
    public const string EnqueuePasswordRecovery = "nexoruta_app.enqueue_password_recovery";
    public const string MarkEmailDelivery = "nexoruta_app.mark_outbox_email_delivery";
    public const string GetEmailDeliveryState = "nexoruta_app.get_outbox_email_delivery_state";
    public const string LoginLookup = "nexoruta_app.login_lookup";
    public const string LookupAccess = "nexoruta_app.lookup_access";
    public const string LookupPlatformAccess = "nexoruta_app.lookup_platform_access";
    public const string LookupUnassignedIdentity = "nexoruta_app.lookup_unassigned_identity";
    public const string PublicTracking = "nexoruta_app.public_tracking";
    public const string CreateShipment = "nexoruta_app.create_shipment";
    public const string ClaimOutbox = "nexoruta_app.claim_outbox";
    public const string FinishOutbox = "nexoruta_app.finish_outbox";
    public const string PreparePush = "nexoruta_app.prepare_push_dispatch";
    public const string RecordPush = "nexoruta_app.record_push_result";
    public const string BeginTrackingJob = "nexoruta_app.begin_tracking_job";
    public const string DeleteUnusedRole = "nexoruta_app.delete_unused_role";
    public const string LockOwnedRole = "nexoruta_app.lock_owned_role";
    public const string RevokeOrgSessions = "nexoruta_app.revoke_org_sessions";
    public const string SetOrganizationState = "nexoruta_app.set_organization_state";

    public static IReadOnlyList<(string Identifier, string Arguments, string Result, string RuntimeRole)> Functions { get; } =
        Array.AsReadOnly<(string Identifier, string Arguments, string Result, string RuntimeRole)>(
        [
            ("nexoruta_app.public_cancellation_email", "text", "text", "nexoruta_api"),
            ("nexoruta_app.issue_public_cancel_code", "text, text, uuid, uuid, text", "text", "nexoruta_api"),
            ("nexoruta_app.confirm_public_cancellation", "text, text, uuid, uuid, uuid", "text", "nexoruta_api"),
            ("nexoruta_app.prepare_public_cancel_email", "uuid", "text", "nexoruta_worker"),
            ("nexoruta_app.mark_public_cancel_email", "uuid", "boolean", "nexoruta_worker"),
            ("nexoruta_app.confirm_shipment_destination", "uuid, bigint, double precision, double precision", "boolean", "nexoruta_api"),
            (CancelShipment, "uuid, bigint, text, uuid, uuid, uuid", "uuid", "nexoruta_api"),
            (LoginLookup, "text",
                "TABLE(\"UsuarioId\" uuid, \"AccesoId\" uuid, \"Email\" text, \"VersionSesion\" bigint, \"EsPlataforma\" boolean, \"TipoOrganizacion\" text, \"EstadoOrganizacion\" text)", "nexoruta_api"),
            (LookupAccess, "uuid",
                "TABLE(\"AccesoId\" uuid, \"UsuarioId\" uuid, \"OperadorId\" uuid, \"ComercioId\" uuid, \"UsuarioEmail\" text, \"OperadorNombre\" text, \"ComercioNombre\" text, \"RolId\" uuid, \"RolCodigo\" text, \"VersionSesion\" bigint, \"Estado\" text)", "nexoruta_api"),
            (LookupPlatformAccess, "uuid",
                "TABLE(\"UsuarioId\" uuid, \"AccesoId\" uuid, \"VersionSesion\" bigint, \"PuedeGestionarOrganizaciones\" boolean, \"UsuarioEmail\" text)", "nexoruta_api"),
            (LookupUnassignedIdentity, "uuid", "TABLE(\"UsuarioId\" uuid, \"Email\" text)", "nexoruta_api"),
            (PublicTracking, "text", "TABLE(\"Estado\" text)", "nexoruta_api"),
            ("nexoruta_app.shipment_rules_for_admission", "uuid", "uuid", "nexoruta_api"),
            (CreateShipment, "uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric, uuid", "uuid", "nexoruta_api"),
            (ClaimOutbox, "uuid, text",
                "TABLE(\"Id\" uuid, \"OperadorId\" uuid, \"EnvioId\" uuid, \"Ambito\" text, \"ClaveAmbito\" uuid, \"Tipo\" text, \"VersionContrato\" integer, \"Destino\" text, \"Prioridad\" text, \"Payload\" text, \"EstadoEntrega\" text, \"Intentos\" integer, \"CreadoEnUtc\" timestamp with time zone)", "nexoruta_worker"),
            (FinishOutbox, "uuid, uuid, boolean, text", "boolean", "nexoruta_worker"),
            (PreparePush, "uuid, uuid", "text", "nexoruta_worker"),
            (RecordPush, "uuid, uuid, uuid, text, text, text", "boolean", "nexoruta_worker"),
            (EnqueuePasswordRecovery, "uuid, uuid, uuid, jsonb, jsonb", "boolean", "nexoruta_api"),
            (MarkEmailDelivery, "uuid, text", "boolean", "nexoruta_worker"),
            (GetEmailDeliveryState, "uuid", "text", "nexoruta_worker"),
            (BeginTrackingJob, "uuid, uuid, uuid", "boolean", "nexoruta_worker"),
            (DeleteUnusedRole, "uuid", "boolean", "nexoruta_api"),
            (LockOwnedRole, "uuid", "boolean", "nexoruta_api"),
            (RevokeOrgSessions, "text, uuid", "integer", "nexoruta_api"),
            (SetOrganizationState, "text, uuid, text, bigint",
                "TABLE(\"EstadoAnterior\" text, \"EstadoNuevo\" text)", "nexoruta_api")
        ]);
}
