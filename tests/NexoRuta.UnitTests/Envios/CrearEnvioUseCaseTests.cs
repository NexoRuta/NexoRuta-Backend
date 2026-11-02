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
        var useCase = new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Operadores(operador));

        var result = await useCase.EjecutarAsync(Comando(operador.OperadorId));

        Assert.Equal(operador.OperadorId, result.Origen.OperadorId);
        Assert.Equal(cuenta.ComercioId, result.Origen.ComercioId);
        Assert.Equal(cuenta.ComercioId, repository.Guardado!.Value.Envio.ComercioId);
        Assert.Equal(cuenta.UsuarioId, result.Origen.CreadoPorUsuarioId);
        Assert.Equal(repository.VersionReglas, repository.Guardado.Value.Envio.VersionReglasId);
        Assert.Equal(operador.Nombre, result.Origen.OperadorNombre);
        Assert.Equal(cuenta.ComercioNombre, result.Origen.ComercioNombre);
        Assert.Equal(result.Id, repository.Guardado!.Value.Envio.Id);
        Assert.Equal(1250m, repository.Guardado.Value.Bultos[0].PesoGramos);
        Assert.Null(cuenta.OperadorId);
    }

    [Fact]
    public async Task Crear_SinPublicacionVigenteNoAdmiteNiPersisteElEnvio()
    {
        var operador = new OperadorDisponible(Guid.NewGuid(), "Operador sin reglas");
        var repo = new FakeEnviosRepository { VersionReglas = null };
        await Assert.ThrowsAsync<NexoRuta.Application.Envios.Exceptions.ReglasEnvioNoDisponiblesException>(() =>
            new CrearEnvioUseCase(repo, new UsuarioActual(CuentaComercio()), new Operadores(operador)).EjecutarAsync(Comando(operador.OperadorId)));
        Assert.Null(repo.Guardado);
    }

    [Fact]
    public async Task Crear_UnUsuarioDelOperadorNoPuedeCrearEnviosDeComercio()
    {
        var cuenta = CuentaOperador();
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<AccesoNoPermitidoException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Operadores(null))
                .EjecutarAsync(Comando(cuenta.OperadorId!.Value)));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_RechazaUnOperadorInexistente()
    {
        var cuenta = CuentaComercio();
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<OperadorNoEncontradoException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Operadores(null))
                .EjecutarAsync(Comando(Guid.CreateVersion7())));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_RequiereQueSeElijaUnOperador()
    {
        var cuenta = CuentaComercio();
        var repository = new FakeEnviosRepository();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Operadores(null))
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
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Operadores(operador))
                .EjecutarAsync(Comando(operador.OperadorId) with { Bultos = [new BultoDeclarado("B-001", 0m, 30m, 20m, 10m)] }));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_SinCuentaActualNoPersisteUnEnvio()
    {
        var repository = new FakeEnviosRepository();
        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioSinCuenta(), new Operadores(null))
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
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Operadores(operador))
                .EjecutarAsync(Comando(operador.OperadorId)));

        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_ComercioVacioNoPersiste()
    {
        var cuenta = CuentaComercio() with { ComercioId = Guid.Empty };
        var repository = new FakeEnviosRepository();
        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() =>
            new CrearEnvioUseCase(repository, new UsuarioActual(cuenta), new Operadores(null))
                .EjecutarAsync(Comando(Guid.CreateVersion7())));
        Assert.Null(repository.Guardado);
    }

    [Fact]
    public async Task Crear_ConservaTodosLosBultosConSusMedidas()
    {
        var operador = new OperadorDisponible(Guid.NewGuid(), "Operador");
        var repo = new FakeEnviosRepository();
        var bultos = new[] { new BultoDeclarado("UNO", 1.25m, 2.5m, 3m, 4m), new BultoDeclarado("DOS", 900m, 20m, 30m, 40m) };
        var result = await new CrearEnvioUseCase(repo, new UsuarioActual(CuentaComercio()), new Operadores(operador))
            .EjecutarAsync(Comando(operador.OperadorId) with { Bultos = bultos });
        Assert.Equal(2, result.Bultos.Count);
        Assert.Equal(bultos.Select(b => b.Codigo), result.Bultos.Select(b => b.Codigo));
        Assert.Equal(bultos.Select(b => b.PesoGramos), repo.Guardado!.Value.Bultos.Select(b => b.PesoGramos));
        Assert.All(repo.Guardado.Value.Bultos, b => Assert.Equal(result.Id, b.EnvioId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crear_RechazaColeccionVaciaOCodigosDuplicadosSinPersistir(bool duplicados)
    {
        var operador = new OperadorDisponible(Guid.NewGuid(), "Operador");
        var repo = new FakeEnviosRepository();
        BultoDeclarado[] bultos = duplicados ? [new("UNO", 1m, 1m, 1m, 1m), new(" UNO ", 2m, 2m, 2m, 2m)] : [];
        await Assert.ThrowsAsync<ArgumentException>(() => new CrearEnvioUseCase(repo,
            new UsuarioActual(CuentaComercio()), new Operadores(operador))
            .EjecutarAsync(Comando(operador.OperadorId) with { Bultos = bultos }));
        Assert.Null(repo.Guardado);
    }

    private static ContextoUsuario CuentaComercio()
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Guid.CreateVersion7(),
            "dueno@comercio.local", null, "Comercio Centro", Guid.CreateVersion7(), "propietario_comercio");

    private static ContextoUsuario CuentaOperador()
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null,
            "operador@empresa.local", "Distribución Sur", null, Guid.CreateVersion7(), "administrador_operador");

    private static CrearEnvioCommand Comando(Guid operadorId)
        => new(OperadorId: operadorId, DestinatarioNombre: "Ana",
                DestinatarioEmail: "destinatario@example.test", Direccion: "18 de Julio 123",
            Bultos: [new BultoDeclarado("B-001", 1250m, 30m, 20m, 10m)], Servicio: new(operadorId, -34.901, -56.201, "Estandar"));

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

    private sealed class Operadores(OperadorDisponible? operador) : IOperadoresRepository
    {
        public Task<OperadorDisponible?> ObtenerAsync(Guid operadorId, CancellationToken cancellationToken = default)
            => Task.FromResult(operador?.OperadorId == operadorId ? operador : null);
        public Task<IReadOnlyList<OperadorDisponible>> ListarAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<OperadorDisponibleParaImportacion>> ListarParaImportacionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeEnviosRepository : IEnviosRepository
    {
        public Guid? VersionReglas { get; init; } = Guid.CreateVersion7();
        public Task<Guid?> ObtenerVersionReglasParaAdmisionAsync(Guid operadorId, CancellationToken cancellationToken = default)
            => Task.FromResult(VersionReglas);

        public (Envio Envio, Destinatario Destinatario, Direccion Direccion, IReadOnlyList<Bulto> Bultos)? Guardado { get; private set; }
        public (Guid? OperadorId, Guid? ComercioId)? Filtro { get; private set; }

        public Task<NexoRuta.Application.Envios.Importacion.IEnviosImportTransaction> IniciarImportacionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task BloquearReferenciasImportacionAsync(IReadOnlyCollection<string> referencias, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<NexoRuta.Application.Envios.Importacion.IdentidadEnvioImportadoEncontrada>> BuscarIdentidadesImportacionAsync(
            IReadOnlyCollection<string> referencias, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa>> GuardarImportadosAsync(IReadOnlyList<NexoRuta.Application.Envios.Importacion.EnvioImportadoParaPersistir> envios,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa> GuardarAsync(Envio envio, Destinatario destinatario, Direccion direccion, IReadOnlyList<Bulto> bultos, ServicioEnvioDeclarado servicio,
            CancellationToken cancellationToken = default)
        {
            Guardado = (envio, destinatario, direccion, bultos);
            return Task.FromResult(NexoRuta.Domain.Tarifas.CalculadoraTarifas.Calcular(
                new(servicio.ZonaId,NexoRuta.Domain.Tarifas.ModalidadTarifaria.Estandar,100m,10m,200m,[]),
                bultos.Select(b=>new NexoRuta.Domain.Tarifas.BultoCalculo(b.PesoGramos,b.LargoCentimetros,b.AnchoCentimetros,b.AltoCentimetros)).ToArray()));
        }

        public Task<IReadOnlyList<EnvioDetalle>> ListarAsync(Guid? operadorId, Guid? comercioId, CancellationToken cancellationToken = default)
        {
            Filtro = (operadorId, comercioId);
            return Task.FromResult<IReadOnlyList<EnvioDetalle>>([]);
        }
    }
}
