namespace NexoRuta.Infrastructure.Notificaciones;
public sealed record AvisoPush(Guid Id, Guid OperadorId, Guid AccesoId, Guid EnvioId);
public sealed class LotePush
{
    public required AvisoPush Aviso { get; init; }
    public required IReadOnlyList<InstalacionMovil> Instalaciones { get; init; }
}
