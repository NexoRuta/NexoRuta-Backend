using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Application.Tarifas.UseCases;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Tarifas;

namespace NexoRuta.UnitTests.Tarifas;

public sealed class TarifarioUseCaseTests
{
    [Fact]
    public async Task QueryDerivesOperatorFromPersistedContextAndRejectsCommerceWithSharedPermission()
    {
        var operatorContext = CreateActor(TipoAccesoUsuario.Operador);
        var repository = new Tarifarios();
        var permissions = new Permissions(true);
        var query = new ConsultarTarifarioUseCase(Guard(operatorContext, permissions), repository);

        await query.EjecutarAsync();

        Assert.Equal(operatorContext.OperadorId, repository.LastOperatorId);
        Assert.Equal(CatalogoPermisos.ConfiguracionGestionar, permissions.LastPermission);

        var commerce = new ContextoUsuario(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
            "commerce@example.test", null, "Commerce", Guid.NewGuid(), "owner");
        var denied = new ConsultarTarifarioUseCase(Guard(commerce, new Permissions(true)), repository);
        await Assert.ThrowsAsync<AccesoCoberturaNoPermitidoException>(() => denied.EjecutarAsync());
        Assert.Equal(operatorContext.OperadorId, repository.LastOperatorId);
    }

    [Fact]
    public async Task SimulationUsesSharedCalculatorAndDoesNotMutateTariffRepository()
    {
        var actor = CreateActor(TipoAccesoUsuario.Operador);
        var userPermissions = new Permissions(true);
        var repository = new Tarifarios();
        var version = VersionTarifario.CrearBorrador(actor.OperadorId!.Value, actor.UsuarioId);
        var rate = TarifaZonaModalidad.Crear(actor.OperadorId.Value, version.Id, Guid.NewGuid(),
            ModalidadTarifaria.Estandar, 100m, 10m, 0m);
        version.ReemplazarTarifas(0, [rate]);
        repository.SimulationData = new(version, rate, true);

        var result = await new SimularTarifaUseCase(Guard(actor, userPermissions), repository).EjecutarAsync(
            new(version.Id, rate.ZonaId, rate.Modalidad, [new(1000m, 10m, 10m, 10m)]));

        Assert.Equal(110m, result.TotalFinalUyu);
        Assert.Equal(version.Id, result.VersionTarifarioId);
        Assert.Null(result.NumeroVersionPublicada);
        Assert.Equal(1, result.VersionConcurrencia);
        Assert.Equal(0, repository.MutationCalls);
    }

    [Fact]
    public async Task SimulationDistinguishesMissingRateFromInactiveZone()
    {
        var actor = CreateActor(TipoAccesoUsuario.Operador);
        var version = VersionTarifario.CrearBorrador(actor.OperadorId!.Value, actor.UsuarioId);
        var repository = new Tarifarios { SimulationData = new(version, null, true) };
        var useCase = new SimularTarifaUseCase(Guard(actor, new Permissions(true)), repository);
        var request = new SolicitudSimulacionTarifario(version.Id, Guid.NewGuid(), ModalidadTarifaria.Urgente,
            [new(1m, 1m, 1m, 1m)]);

        await Assert.ThrowsAsync<TarifaNoConfiguradaException>(() => useCase.EjecutarAsync(request));
        repository.SimulationData = new(version, null, false);
        var inactive = await Assert.ThrowsAsync<ConflictoTarifarioException>(() => useCase.EjecutarAsync(request));
        Assert.Equal(ReglasTarifario.Errors.ZoneInactive, inactive.Code);
    }

    private static RequerirOperadorCoberturaUseCase Guard(ContextoUsuario context, Permissions permissions)
        => new(new CurrentUser(context), permissions);

    private static ContextoUsuario CreateActor(TipoAccesoUsuario type)
    {
        var userId = Guid.NewGuid();
        var accessId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        return type == TipoAccesoUsuario.Operador
            ? new(accessId, userId, organizationId, null, "operator@example.test", "Operator", null, Guid.NewGuid(), "admin")
            : new(accessId, userId, null, organizationId, "commerce@example.test", null, "Commerce", Guid.NewGuid(), "owner");
    }

    private sealed class CurrentUser(ContextoUsuario actor) : IUsuarioActual
    {
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(actor);
    }

    private sealed class Permissions(bool allowed) : IPermisosUsuarioRepository
    {
        public string? LastPermission { get; private set; }
        public Task<bool> TienePermisoAsync(Guid usuarioId, Guid accesoId, string codigoPermiso,
            CancellationToken cancellationToken = default)
        {
            LastPermission = codigoPermiso;
            return Task.FromResult(allowed);
        }

        public Task<IReadOnlyList<string>> ListarCodigosAsync(Guid usuarioId, Guid accesoId,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class Tarifarios : ITarifariosRepository
    {
        public Guid? LastOperatorId { get; private set; }
        public int MutationCalls { get; private set; }
        public DatosSimulacionTarifario? SimulationData { get; set; }

        public Task<TarifarioConsulta> ConsultarAsync(Guid operadorId, CancellationToken cancellationToken = default)
        {
            LastOperatorId = operadorId;
            return Task.FromResult(new TarifarioConsulta(null, []));
        }

        public Task<VersionTarifario> CrearBorradorAsync(Guid operadorId, Guid usuarioId,
            CancellationToken cancellationToken = default) => Mutate();

        public Task<VersionTarifario> EditarBorradorAsync(Guid operadorId, Guid usuarioId, long versionEsperada,
            IReadOnlyCollection<TarifaZonaModalidadConfiguracion> configuracion,
            CancellationToken cancellationToken = default) => Mutate();

        public Task<VersionTarifario> PublicarBorradorAsync(Guid operadorId, Guid usuarioId, long versionEsperada,
            CancellationToken cancellationToken = default) => Mutate();

        public Task<DatosSimulacionTarifario?> ObtenerParaSimularAsync(Guid operadorId, Guid versionId, Guid zonaId,
            ModalidadTarifaria modalidad, CancellationToken cancellationToken = default)
        {
            LastOperatorId = operadorId;
            return Task.FromResult(SimulationData);
        }

        private Task<VersionTarifario> Mutate()
        {
            MutationCalls++;
            throw new InvalidOperationException("Simulation must not mutate the tariff repository.");
        }
    }
}
