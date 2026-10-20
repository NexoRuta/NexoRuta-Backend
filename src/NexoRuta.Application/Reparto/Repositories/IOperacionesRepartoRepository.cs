using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Application.Reparto.Repositories;
public interface IOperacionesRepartoRepository
{
    Task<ResultadoOperacionReparto?> AplicarAsync(Guid operadorId, Guid accesoId, Guid jornadaId, OperacionRepartoDatos operacion, CancellationToken ct);
    Task<bool> GuardarEvidenciaAsync(Guid operadorId, Guid accesoId, Guid jornadaId, Guid envioId,
        Guid archivoId, string tipo, byte[] contenido, CancellationToken ct);
}
