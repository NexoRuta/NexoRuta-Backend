using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Domain.Seguimiento;

namespace NexoRuta.Application.Seguimiento.UseCases;

public sealed class CrearEnlaceSeguimientoUseCase(
    IConsultaEstadoEnvio consultaEstado,
    IEnlacesSeguimientoRepository repositorio)
{
    public async Task<string> EjecutarAsync(
        Guid operadorId,
        Guid envioId,
        CancellationToken cancellationToken = default)
    {
        var estado = await consultaEstado.ObtenerEstadoAsync(
            operadorId,
            envioId,
            cancellationToken);

        if (estado is null)
            throw new InvalidOperationException(
                "No existe el envío para el operador indicado.");

        var token = GeneradorTokenSeguimiento.Generar();


        var tokenHash = GeneradorTokenSeguimiento.CalcularHash(token);


        var ahora = DateTimeOffset.UtcNow;

        var enlace = new EnlaceSeguimiento(
            operadorId,
            envioId,
            tokenHash,
            ahora,
            ahora.AddHours(24));

        await repositorio.GuardarAsync(
            enlace,
            cancellationToken);

        return token;
    }
}