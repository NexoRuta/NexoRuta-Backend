using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.Dtos;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Seguimiento.UseCases;

public sealed class ConsultarSeguimientoPublicoUseCase(
    IEnlacesSeguimientoRepository enlaces)
{
    public async Task<EstadoEnvio?> EjecutarAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {

        if (!TieneFormatoValido(token))
            return null;

        return await enlaces.ObtenerEstadoPublicoAsync(token!, cancellationToken);
    }

    public Task<SeguimientoPublico?> EjecutarConIdentidadAsync(
        string? token,
        CancellationToken cancellationToken = default)
        => TieneFormatoValido(token)
            ? enlaces.ObtenerSeguimientoPublicoAsync(token!, cancellationToken)
            : Task.FromResult<SeguimientoPublico?>(null);

    private static bool TieneFormatoValido(string? token)
    {
        if (token is null || token.Length != 64)
            return false;

        return token.All(c =>
            (c >= '0' && c <= '9') ||
            (c >= 'a' && c <= 'f'));
    }
}
