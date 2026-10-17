using NexoRuta.Domain.Notificaciones;
namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class LotePush
{
    public required AvisoReparto Aviso { get; init; }
    public required IReadOnlyList<InstalacionMovil> Instalaciones { get; init; }
}
