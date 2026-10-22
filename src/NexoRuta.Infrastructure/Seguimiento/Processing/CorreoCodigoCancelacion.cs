using NexoRuta.Application.Seguimiento.Dtos;

namespace NexoRuta.Infrastructure.Seguimiento.Processing;

public sealed record CorreoCodigoCancelacion(Guid MessageId, Guid CodigoId, string Destinatario, string Codigo,
    IdentidadVisualPublica? Identidad = null)
{
    public const string ProtectionPurpose = "NexoRuta.RecipientCancellation.Email.v1";
}
