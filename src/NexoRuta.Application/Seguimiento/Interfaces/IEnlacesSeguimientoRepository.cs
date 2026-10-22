using NexoRuta.Domain.Seguimiento;
using NexoRuta.Domain.Envios;
using NexoRuta.Application.Seguimiento.Dtos;

namespace NexoRuta.Application.Seguimiento.Interfaces;

public interface IEnlacesSeguimientoRepository
{
    Task GuardarAsync(
        EnlaceSeguimiento enlace,
        CancellationToken cancellationToken = default);

    Task<EnlaceSeguimiento?> BuscarPorHashAsync(
    string tokenHash,
    CancellationToken cancellationToken = default);

    Task<EstadoEnvio?> ObtenerEstadoPublicoAsync(string token, CancellationToken cancellationToken = default);
    Task<SeguimientoPublico?> ObtenerSeguimientoPublicoAsync(string token, CancellationToken cancellationToken = default);
}
