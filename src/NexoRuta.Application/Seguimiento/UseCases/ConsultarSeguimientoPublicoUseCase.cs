using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Seguimiento.UseCases;

public sealed class ConsultarSeguimientoPublicoUseCase(
    IEnlacesSeguimientoRepository enlaces,
    IConsultaEstadoEnvio consultaEstado)
{
    public async Task<EstadoEnvio?> EjecutarAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {

        if (!TieneFormatoValido(token))
            return null;

        var tokenHash =
            GeneradorTokenSeguimiento.CalcularHash(token!);

        var enlace = await enlaces.BuscarPorHashAsync(
            tokenHash,
            cancellationToken);

        if (enlace is null ||
            !enlace.EstaVigente(DateTimeOffset.UtcNow))
        {
            return null;
        }

        return await consultaEstado.ObtenerEstadoAsync(
            enlace.OperadorId,
            enlace.EnvioId,
            cancellationToken);
    }

    private static bool TieneFormatoValido(string? token)
    {
        if (token is null || token.Length != 64)
            return false;

        return token.All(c =>
            (c >= '0' && c <= '9') ||
            (c >= 'a' && c <= 'f'));
    }
}