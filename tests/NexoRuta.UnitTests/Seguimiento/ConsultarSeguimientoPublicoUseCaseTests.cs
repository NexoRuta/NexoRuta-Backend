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
        var casoDeUso = new ConsultarSeguimientoPublicoUseCase(repositorio);

        // Act
        var resultado = await casoDeUso.EjecutarAsync(token);

        // Assert
        Assert.Equal(EstadoEnvio.Admitido, resultado);
        Assert.Equal(1, repositorio.CantidadConsultasPublicas);
        Assert.Equal(token, repositorio.TokenConsultado);
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
        var casoDeUso = new ConsultarSeguimientoPublicoUseCase(repositorio);

        // Act
        var resultado = await casoDeUso.EjecutarAsync(token);

        // Assert
        Assert.Null(resultado);
        Assert.Equal(1, repositorio.CantidadConsultasPublicas);
    }

    private sealed class RepositorioFake(
     EnlaceSeguimiento? enlace
 ) : IEnlacesSeguimientoRepository
    {
        public int CantidadConsultasPublicas { get; private set; }
        public string? TokenConsultado { get; private set; }

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

        public Task<EstadoEnvio?> ObtenerEstadoPublicoAsync(string token, CancellationToken cancellationToken = default)
        {
            CantidadConsultasPublicas++;
            TokenConsultado = token;
            var valid = enlace?.TokenHash == GeneradorTokenSeguimiento.CalcularHash(token) &&
                enlace.EstaVigente(DateTimeOffset.UtcNow);
            return Task.FromResult<EstadoEnvio?>(valid ? EstadoEnvio.Admitido : null);
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
        var casoDeUso = new ConsultarSeguimientoPublicoUseCase(repositorio);

        // Act
        var resultado = await casoDeUso.EjecutarAsync(token);

        // Assert
        Assert.Null(resultado);
        Assert.Equal(1, repositorio.CantidadConsultasPublicas);
    }
}
