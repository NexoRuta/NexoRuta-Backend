namespace NexoRuta.Application.Notificaciones.Repositories;
public sealed record AvisoComercioDatos(Guid EventoId, Guid EnvioId, string Tipo, int VersionContrato, string Datos, DateTimeOffset RecibidoEnUtc);
public interface IAvisosComercioRepository
{
    Task<IReadOnlyList<AvisoComercioDatos>> ListarAsync(CancellationToken ct);
}
