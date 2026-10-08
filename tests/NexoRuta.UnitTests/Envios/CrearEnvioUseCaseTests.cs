using NexoRuta.Application.Administracion;
using NexoRuta.Application.Envios;
using NexoRuta.Domain.Envios;

namespace NexoRuta.UnitTests.Envios;

public sealed class CrearEnvioUseCaseTests
{
    [Fact]
    public async Task Crear_UsaLaCuentaDelComercioYElOperadorElegido()
    {
        var cuenta = CuentaComercio();
        var operador = new OperadorDisponible(Guid.CreateVersion7(), Guid.CreateVersion7(), "Distribución Sur");
        var repository = new FakeEnviosRepository();
        var useCase = new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(cuenta.ComercioId!.Value, operador));

        var result = await useCase.EjecutarAsync(Comando(operador.OperadorId));

        Assert.Equal(operador.OperadorId, result.OperadorId);
        Assert.Equal(operador.OperadorComercioId, result.OperadorComercioId);
        Assert.Equal(cuenta.UsuarioId, result.CreadoPorUsuarioId);
        Assert.Equal(operador.Nombre, result.OperadorNombre);
        Assert.Equal(cuenta.ComercioNombre, result.ComercioNombre);
        Assert.Equal(result.Id, repository.Guardado!.Value.Envio.Id);
        Assert.Equal(1250m, repository.Guardado.Value.Bulto.PesoGramos);
        Assert.Null(cuenta.OperadorId);
    }

    [Fact]
    public async Task Crear_UnUsuarioDelOperadorNoPuedeCrearEnviosDeComercio()
    {
        var cuenta = CuentaOperador();
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<AccesoNoPermitidoException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(Guid.CreateVersion7(), null))
                .EjecutarAsync(Comando(cuenta.OperadorId!.Value)));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_RechazaUnOperadorSinRelacionConElComercio()
    {
        var cuenta = CuentaComercio();
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<OperadorNoVinculadoException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(cuenta.ComercioId!.Value, null))
                .EjecutarAsync(Comando(Guid.CreateVersion7())));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_RequiereQueSeElijaUnOperador()
    {
        var cuenta = CuentaComercio();
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(cuenta.ComercioId!.Value, null))
                .EjecutarAsync(Comando(Guid.Empty)));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_RechazaPesoNoPositivoAntesDePersistir()
    {
        var cuenta = CuentaComercio();
        var operador = new OperadorDisponible(Guid.CreateVersion7(), Guid.CreateVersion7(), "Distribución Sur");
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(cuenta.ComercioId!.Value, operador))
                .EjecutarAsync(Comando(operador.OperadorId) with { PesoGramos = 0m }));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_SinCuentaActualNoPersisteUnEnvio()
    {
        var repository = new FakeEnviosRepository();
        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioSinCuenta(), new Cuentas(Guid.CreateVersion7(), null))
                .EjecutarAsync(Comando(Guid.CreateVersion7())));
        Assert.Null(repository.Guardado);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Listar_FiltraPorLaOrganizacionDeLaCuenta(bool esComercio)
    {
        var cuenta = esComercio ? CuentaComercio() : CuentaOperador();
        var repository = new FakeEnviosRepository();
        await new ListarEnviosUseCase(repository, new UsuarioActual(cuenta)).EjecutarAsync();
        Assert.Equal((cuenta.OperadorId, cuenta.ComercioId), repository.Filtro);
    }

    private static ContextoUsuario CuentaComercio()
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Guid.CreateVersion7(),
            "dueno@comercio.local", null, "Comercio Centro", true);

    private static ContextoUsuario CuentaOperador()
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null,
            "operador@empresa.local", "Distribución Sur", null, false);

    private static CrearEnvioCommand Comando(Guid operadorId)
        => new(operadorId, "Ana", "18 de Julio 123", "B-001", 1250m, 30m, 20m, 10m);

    private sealed class UsuarioActual(ContextoUsuario cuenta) : IUsuarioActual
    {
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(cuenta);
    }

    private sealed class UsuarioSinCuenta : IUsuarioActual
    {
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
            => throw new AccesoActualNoDisponibleException();
    }

    private sealed class Cuentas(Guid comercioId, OperadorDisponible? operador) : IAccesosUsuarioRepository
    {
        public Task<OperadorDisponible?> ObtenerOperadorAsync(Guid comercio, Guid operadorId, CancellationToken cancellationToken = default)
            => Task.FromResult(comercio == comercioId && operador?.OperadorId == operadorId ? operador : null);
        public Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ContextoUsuario>> ListarAsync(NexoRuta.Domain.Administracion.TipoAccesoUsuario tipo, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<OperadorDisponible>> ListarOperadoresAsync(Guid comercio, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeEnviosRepository : IEnviosRepository
    {
        public (Envio Envio, Destinatario Destinatario, Direccion Direccion, Bulto Bulto)? Guardado { get; private set; }
        public (Guid? OperadorId, Guid? ComercioId)? Filtro { get; private set; }

        public Task GuardarAsync(Envio envio, Destinatario destinatario, Direccion direccion, Bulto bulto,
            CancellationToken cancellationToken = default)
        {
            Guardado = (envio, destinatario, direccion, bulto);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EnvioDetalle>> ListarAsync(Guid? operadorId, Guid? comercioId, CancellationToken cancellationToken = default)
        {
            Filtro = (operadorId, comercioId);
            return Task.FromResult<IReadOnlyList<EnvioDetalle>>([]);
        }
    }
}
