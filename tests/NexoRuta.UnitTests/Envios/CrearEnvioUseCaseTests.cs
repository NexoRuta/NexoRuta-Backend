using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Domain.Envios;

namespace NexoRuta.UnitTests.Envios;

public sealed class CrearEnvioUseCaseTests
{
    [Fact]
    public async Task Crear_UsaLaCuentaDelComercioYElOperadorElegido()
    {
        var cuenta = CuentaComercio();
        var operador = new OperadorDisponible(Guid.CreateVersion7(), "Distribución Sur");
        var repository = new FakeEnviosRepository();
        var useCase = new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(operador));

        var result = await useCase.EjecutarAsync(Comando(operador.OperadorId));

        Assert.Equal(operador.OperadorId, result.Origen.OperadorId);
        Assert.Equal(cuenta.ComercioId, result.Origen.ComercioId);
        Assert.Equal(cuenta.ComercioId, repository.Guardado!.Value.Envio.ComercioId);
        Assert.Equal(cuenta.UsuarioId, result.Origen.CreadoPorUsuarioId);
        Assert.Equal(operador.Nombre, result.Origen.OperadorNombre);
        Assert.Equal(cuenta.ComercioNombre, result.Origen.ComercioNombre);
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
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(null))
                .EjecutarAsync(Comando(cuenta.OperadorId!.Value)));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_RechazaUnOperadorInexistente()
    {
        var cuenta = CuentaComercio();
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<OperadorNoDisponibleException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(null))
                .EjecutarAsync(Comando(Guid.CreateVersion7())));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_RequiereQueSeElijaUnOperador()
    {
        var cuenta = CuentaComercio();
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(null))
                .EjecutarAsync(Comando(Guid.Empty)));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_RechazaPesoNoPositivoAntesDePersistir()
    {
        var cuenta = CuentaComercio();
        var operador = new OperadorDisponible(Guid.CreateVersion7(), "Distribución Sur");
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(operador))
                .EjecutarAsync(Comando(operador.OperadorId) with { PesoGramos = 0m }));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_SinCuentaActualNoPersisteUnEnvio()
    {
        var repository = new FakeEnviosRepository();
        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioSinCuenta(), new Cuentas(null))
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Crear_ContextoDeComercioIncompletoNoPersiste(string? nombre)
    {
        var cuenta = CuentaComercio() with { ComercioNombre = nombre };
        var operador = new OperadorDisponible(Guid.CreateVersion7(), "Operador");
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(operador))
                .EjecutarAsync(Comando(operador.OperadorId)));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_ComercioVacioNoPersiste()
    {
        var cuenta = CuentaComercio() with { ComercioId = Guid.Empty };
        var repository = new FakeEnviosRepository();
        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Cuentas(null))
                .EjecutarAsync(Comando(Guid.CreateVersion7())));
        Assert.Null(repository.Guardado);
    }

    private static ContextoUsuario CuentaComercio()
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Guid.CreateVersion7(),
            "dueno@comercio.local", null, "Comercio Centro", true);

    private static ContextoUsuario CuentaOperador()
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null,
            "operador@empresa.local", "Distribución Sur", null, false);

    private static CrearEnvioCommand Comando(Guid operadorId)
        => new(OperadorId: operadorId, DestinatarioNombre: "Ana", Direccion: "18 de Julio 123",
            CodigoBulto: "B-001", PesoGramos: 1250m, LargoCentimetros: 30m, AnchoCentimetros: 20m, AltoCentimetros: 10m);

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

    private sealed class Cuentas(OperadorDisponible? operador) : IAccesosUsuarioRepository
    {
        public Task<OperadorDisponible?> ObtenerOperadorAsync(Guid operadorId, CancellationToken cancellationToken = default)
            => Task.FromResult(operador?.OperadorId == operadorId ? operador : null);
        public Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ContextoUsuario>> ListarAsync(NexoRuta.Domain.Administracion.TipoAccesoUsuario tipo, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<OperadorDisponible>> ListarOperadoresAsync(CancellationToken cancellationToken = default)
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
