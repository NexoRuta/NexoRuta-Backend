using NexoRuta.Domain.Seguimiento;

namespace NexoRuta.Application.Seguimiento.Interfaces;

public interface IEnlacesSeguimientoRepository
{
    Task GuardarAsync(
        EnlaceSeguimiento enlace,
        CancellationToken cancellationToken = default);

    Task<EnlaceSeguimiento?> BuscarPorHashAsync(
    string tokenHash,
    CancellationToken cancellationToken = default);
}