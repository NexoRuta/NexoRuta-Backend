using NexoRuta.Application.Notificaciones.Dtos;
namespace NexoRuta.Api.Features.Notificaciones.Contracts;
public sealed record AvisoRepartoResponse(Guid Id, Guid RutaId, Guid EnvioId, long VersionEnvio,
    string Tipo, string Motivo, DateTimeOffset CreadoEnUtc, bool Vigente, DateTimeOffset? ConfirmadoEnUtc)
{
    public static AvisoRepartoResponse Desde(AvisoRepartoDatos a) => new(a.Id, a.RutaId, a.EnvioId, a.VersionEnvio, a.Tipo, a.Motivo, a.CreadoEnUtc, a.Vigente, a.ConfirmadoEnUtc);
}
