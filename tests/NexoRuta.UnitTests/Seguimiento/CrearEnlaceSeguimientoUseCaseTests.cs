using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;

namespace NexoRuta.UnitTests.Seguimiento;

public sealed class CrearEnlaceSeguimientoUseCaseTests
{
    [Fact]
    public async Task EjecutarAsync_GuardaEnlaceYDevuelveToken()
    {
        // Arrange
        var operadorId = Guid.NewGuid();
        var envioId = Guid.NewGuid();

        var consulta = new ConsultaEstadoFake();
        var repositorio = new RepositorioFake();

        var casoDeUso = new CrearEnlaceSeguimientoUseCase(
            consulta,
            repositorio);

        // Act
        var token = await casoDeUso.EjecutarAsync(
            operadorId,
            envioId);

        // Assert
        Assert.NotNull(repositorio.EnlaceGuardado);

        Assert.Equal(
            operadorId,
            repositorio.EnlaceGuardado.OperadorId);

        Assert.Equal(
            envioId,
            repositorio.EnlaceGuardado.EnvioId);

        Assert.Equal(
            GeneradorTokenSeguimiento.CalcularHash(token),
            repositorio.EnlaceGuardado.TokenHash);
    }
    [Fact]
    public async Task EjecutarAsync_SiEnvioNoExiste_NoDebeGuardarEnlace()
    {
        // Arrange
        var operadorId = Guid.NewGuid();
        var envioId = Guid.NewGuid();

        var consulta = new ConsultaEstadoFake(null);
        var repositorio = new RepositorioFake();

        var casoDeUso = new CrearEnlaceSeguimientoUseCase(
            consulta,
            repositorio);

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => casoDeUso.EjecutarAsync(
                operadorId,
                envioId));

        Assert.Null(repositorio.EnlaceGuardado);
    }
    private sealed class ConsultaEstadoFake(
        EstadoEnvio? estado = EstadoEnvio.Admitido
    ) : IConsultaEstadoEnvio
    {
        public Task<EstadoEnvio?> ObtenerEstadoAsync(
            Guid operadorId,
            Guid envioId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(estado);
        }
    }

    private sealed class RepositorioFake : IEnlacesSeguimientoRepository
    {
        public EnlaceSeguimiento? EnlaceGuardado { get; private set; }

        public Task GuardarAsync(
            EnlaceSeguimiento enlace,
            CancellationToken cancellationToken = default)
        {
            EnlaceGuardado = enlace;

            return Task.CompletedTask;
        }
    public Task<EnlaceSeguimiento?> BuscarPorHashAsync(
    string tokenHash,
    CancellationToken cancellationToken = default)
    {
        EnlaceSeguimiento? resultado =
            EnlaceGuardado?.TokenHash == tokenHash
                ? EnlaceGuardado
                : null;

        return Task.FromResult(resultado);
    }
    }
}