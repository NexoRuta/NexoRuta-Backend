namespace NexoRuta.Infrastructure.Seguimiento.Processing;

public sealed record CorreoCodigoCancelacion(Guid MessageId, Guid CodigoId, string Destinatario, string Codigo)
{
    public const string ProtectionPurpose = "NexoRuta.RecipientCancellation.Email.v1";
}
