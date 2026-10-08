using NexoRuta.Application.Envios;
using NexoRuta.Domain.Envios;

namespace NexoRuta.UnitTests.Envios;

public sealed class CrearEnvioUseCaseTests
{
    [Fact]
    public async Task EjecutarAsync_UsaElAccesoDeDemoDelServidorParaAsignarElEnvio()
    {
        var usuarioId = Guid.CreateVersion7();
        var operadorId = Guid.CreateVersion7();
        var relacionId = Guid.CreateVersion7();
        var repository = new FakeEnviosRepository(new DemoComercioContext(
            usuarioId, operadorId, relacionId, "demo@nexoruta.local", "Operador Demo", "Comercio Demo"));
        var useCase = new CrearEnvioUseCase(repository);

        var result = await useCase.EjecutarAsync(new CrearEnvioCommand(
            "Ana Destinataria", "18 de Julio 123", "BULTO-001", 1250m, 30m, 20m, 10m));

        Assert.Equal(operadorId, result.OperadorId);
        Assert.Equal(relacionId, result.OperadorComercioId);
        Assert.Equal(usuarioId, result.CreadoPorUsuarioId);
        Assert.Equal("Comercio Demo", result.ComercioNombre);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.NotNull(repository.Guardado);
        Assert.Equal(result.Id, repository.Guardado.Value.Envio.Id);
        Assert.Equal("BULTO-001", repository.Guardado.Value.Bulto.Codigo);
        Assert.Equal(1250m, repository.Guardado.Value.Bulto.PesoGramos);
        Assert.Equal(30m, repository.Guardado.Value.Bulto.LargoCentimetros);
    }

    [Fact]
    public async Task EjecutarAsync_RechazaPesoNoPositivoAntesDePersistir()
    {
        var repository = new FakeEnviosRepository(new DemoComercioContext(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            "demo@nexoruta.local", "Operador Demo", "Comercio Demo"));
        var useCase = new CrearEnvioUseCase(repository);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => useCase.EjecutarAsync(new CrearEnvioCommand(
            "Ana", "18 de Julio 123", "BULTO-002", 0m, 30m, 20m, 10m)));

        Assert.Null(repository.Guardado);
    }

    private sealed class FakeEnviosRepository(DemoComercioContext context) : IEnviosRepository
    {
        public (Envio Envio, Destinatario Destinatario, Direccion Direccion, Bulto Bulto)? Guardado { get; private set; }

        public Task<DemoComercioContext?> ObtenerContextoDemoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DemoComercioContext?>(context);

        public Task GuardarAsync(
            Envio envio,
            Destinatario destinatario,
            Direccion direccion,
            Bulto bulto,
            CancellationToken cancellationToken = default)
        {
            Guardado = (envio, destinatario, direccion, bulto);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EnvioDetalle>> ListarAsync(
            DemoComercioContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EnvioDetalle>>([]);
    }
}
