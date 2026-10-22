namespace NexoRuta.Application.Notificaciones.Dtos;
public sealed record AvisoRepartoDatos(Guid Id, Guid RutaId, Guid EnvioId, long VersionEnvio,
    string Tipo, string Motivo, DateTimeOffset CreadoEnUtc, bool Vigente, DateTimeOffset? ConfirmadoEnUtc);
