using System.ComponentModel.DataAnnotations;
namespace NexoRuta.Api.Features.Notificaciones.Contracts;
public sealed class RegistroInstalacionRequest
{
    public Guid InstalacionId { get; init; }
    [Required, StringLength(10)] public string Plataforma { get; init; } = "";
    [Required, StringLength(128, MinimumLength = 20), RegularExpression("^[A-Za-z0-9_-]{20,128}$")] public string FirebaseInstallationId { get; init; } = "";
    public bool Permitida { get; init; }
}
