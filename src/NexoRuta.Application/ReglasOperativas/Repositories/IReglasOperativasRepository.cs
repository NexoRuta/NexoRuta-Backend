using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.Application.ReglasOperativas.Repositories;

public interface IReglasOperativasRepository
{
    Task<ReglasOperativasConsulta> ConsultarAsync(Guid operadorId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<VersionReglasOperativas> Items, int Total)> ListarPublicacionesAsync(
        Guid operadorId, int pagina, int tamanoPagina, CancellationToken cancellationToken = default);
    Task<VersionReglasOperativas?> ObtenerPublicacionAsync(Guid operadorId, Guid versionId,
        CancellationToken cancellationToken = default);
    Task<VersionReglasOperativas> CrearBorradorAsync(Guid operadorId, Guid usuarioId,
        CancellationToken cancellationToken = default);
    Task<VersionReglasOperativas> EditarBorradorAsync(Guid operadorId, Guid usuarioId, long versionEsperada,
        ReglasOperativasConfiguracion configuracion, CancellationToken cancellationToken = default);
    Task<VersionReglasOperativas> PublicarBorradorAsync(Guid operadorId, Guid usuarioId, long versionEsperada,
        CancellationToken cancellationToken = default);
}

public sealed record ReglasOperativasConsulta(
    ReglasOperativasOperador? Operador,
    VersionReglasOperativas? Borrador,
    VersionReglasOperativas? PublicacionVigente);
