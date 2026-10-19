using NexoRuta.Domain.Administracion;

namespace NexoRuta.Infrastructure.Envios;

public sealed class CancelacionEnvio
{
    public Guid Id { get; init; }
    public Guid OperadorId { get; init; }
    public Guid EnvioId { get; init; }
    public Guid? AccesoId { get; init; }
    public Guid? UsuarioId { get; init; }
    public TipoAccesoUsuario? TipoActor { get; init; }
    public Guid? OrganizacionActorId { get; init; }
    public Guid? AutorizacionPublicaId { get; init; }
    public string Motivo { get; init; } = "";
    public string EstadoAnterior { get; init; } = "";
    public long VersionAnterior { get; init; }
    public long VersionEnvio { get; init; }
    public DateTimeOffset CreadaEnUtc { get; init; }
}
