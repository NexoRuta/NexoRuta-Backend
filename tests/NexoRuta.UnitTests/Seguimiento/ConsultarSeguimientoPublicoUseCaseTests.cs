using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;

namespace NexoRuta.UnitTests.Seguimiento;

public sealed class ConsultarSeguimientoPublicoUseCaseTests
{
    [Fact]
    public async Task EjecutarAsync_TokenVigente_DevuelveEstado()
    {
        // Arrange
        var operadorId = Guid.NewGuid();
        var envioId = Guid.NewGuid();

        var token = GeneradorTokenSeguimiento.Generar();
        var hash = GeneradorTokenSeguimiento.CalcularHash(token);

        var ahora = DateTimeOffset.UtcNow;

        var enlace = new EnlaceSeguimiento(
            operadorId,
            envioId,
            hash,
            ahora.AddMinutes(-1),
            ahora.AddHours(1));

        var repositorio = new RepositorioFake(enlace);
        var consulta = new ConsultaEstadoFake();

        var casoDeUso = new ConsultarSeguimientoPublicoUseCase(
            repositorio,
            consulta);

        // Act
        var resultado = await casoDeUso.EjecutarAsync(token);

        // Assert
        Assert.Equal(EstadoEnvio.Admitido, resultado);
        Assert.Equal(1, consulta.CantidadConsultas);
        Assert.Equal(operadorId, consulta.OperadorConsultado);
        Assert.Equal(envioId, consulta.EnvioConsultado);
    }

    [Fact]
    public async Task EjecutarAsync_TokenVencido_NoConsultaEnvio()
    {
        // Arrange
        var operadorId = Guid.NewGuid();
        var envioId = Guid.NewGuid();

        var token = GeneradorTokenSeguimiento.Generar();
        var hash = GeneradorTokenSeguimiento.CalcularHash(token);

        var ahora = DateTimeOffset.UtcNow;

        var enlace = new EnlaceSeguimiento(
            operadorId,
            envioId,
            hash,
            ahora.AddHours(-3),
            ahora.AddHours(-2));

        var repositorio = new RepositorioFake(enlace);
        var consulta = new ConsultaEstadoFake();

        var casoDeUso = new ConsultarSeguimientoPublicoUseCase(
            repositorio,
            consulta);

        // Act
        var resultado = await casoDeUso.EjecutarAsync(token);

        // Assert
        Assert.Null(resultado);
        Assert.Equal(0, consulta.CantidadConsultas);
    }

    private sealed class RepositorioFake(
     EnlaceSeguimiento? enlace
 ) : IEnlacesSeguimientoRepository
    {
        public Task GuardarAsync(
            EnlaceSeguimiento nuevoEnlace,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<EnlaceSeguimiento?> BuscarPorHashAsync(
            string tokenHash,
            CancellationToken cancellationToken = default)
        {
            EnlaceSeguimiento? resultado =
                enlace?.TokenHash == tokenHash
                    ? enlace
                    : null;

            return Task.FromResult(resultado);
        }
    }
    [Fact]
    public async Task EjecutarAsync_TokenRevocado_NoConsultaEnvio()
    {
        // Arrange
        var token = GeneradorTokenSeguimiento.Generar();
        var hash = GeneradorTokenSeguimiento.CalcularHash(token);

        var ahora = DateTimeOffset.UtcNow;

        var enlace = new EnlaceSeguimiento(
            Guid.NewGuid(),
            Guid.NewGuid(),
            hash,
            ahora.AddHours(-1),
            ahora.AddHours(1));

        // Revocamos el enlace antes de su vencimiento.
        enlace.Revocar(ahora);

        var repositorio = new RepositorioFake(enlace);
        var consulta = new ConsultaEstadoFake();

        var casoDeUso = new ConsultarSeguimientoPublicoUseCase(
            repositorio,
            consulta);

        // Act
        var resultado = await casoDeUso.EjecutarAsync(token);

        // Assert
        Assert.Null(resultado);
        Assert.Equal(0, consulta.CantidadConsultas);
    }
    private sealed class ConsultaEstadoFake : IConsultaEstadoEnvio
    {
        public int CantidadConsultas { get; private set; }

        public Guid? OperadorConsultado { get; private set; }

        public Guid? EnvioConsultado { get; private set; }

        public Task<EstadoEnvio?> ObtenerEstadoAsync(
            Guid operadorId,
            Guid envioId,
            CancellationToken cancellationToken = default)
        {
            CantidadConsultas++;

            OperadorConsultado = operadorId;
            EnvioConsultado = envioId;

            return Task.FromResult<EstadoEnvio?>(
                EstadoEnvio.Admitido);
        }
    }
}