using NetTopologySuite;
using NetTopologySuite.Geometries;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Application.ZonasCobertura;
using NexoRuta.Application.ZonasCobertura.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ZonasCobertura;

namespace NexoRuta.UnitTests.ZonasCobertura;

public sealed class ZonaCoberturaUseCaseTests
{
    [Fact]
    public async Task CreationUsesPersistedOperatorAndRequiresExistingConfigurationPermission()
    {
        var actor = OperatorContext();
        var current = new CurrentUser(actor);
        var permissions = new Permissions(true);
        var repository = new Zones();
        var geometry = GeometryFactory().CreatePolygon(GeometryFactory().CreateLinearRing(
        [new(-56, -34), new(-55, -34), new(-55, -33), new(-56, -34)]));

        var access = new RequerirOperadorCoberturaUseCase(current, permissions);
        await new CrearZonaCoberturaUseCase(access, repository).EjecutarAsync("Centro", geometry);

        Assert.Equal((actor.UsuarioId, actor.AccesoId, CatalogoPermisos.ConfiguracionGestionar), permissions.Requested);
        Assert.Equal(actor.OperadorId, repository.Created!.OperadorId);
        Assert.Equal(actor.UsuarioId, repository.CreatedBy);
    }

    [Fact]
    public async Task QueryRejectsCommerceEvenWhenItHasTheSharedPermission()
    {
        var commerce = new ContextoUsuario(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
            "commerce@example.invalid", null, "Commerce", Guid.NewGuid(), "owner");
        var permissions = new Permissions(true);
        var repository = new Zones();

        await Assert.ThrowsAsync<AccesoCoberturaNoPermitidoException>(() =>
            new ConsultarCoberturaUseCase(new RequerirOperadorCoberturaUseCase(new CurrentUser(commerce), permissions), repository).EjecutarAsync(-56, -34));
        Assert.Null(permissions.Requested);
    }

    [Fact]
    public async Task QueryRequiresDynamicConfigurationPermission()
    {
        var permissions = new Permissions(false);
        var repository = new Zones();

        await Assert.ThrowsAsync<AccesoCoberturaNoPermitidoException>(() =>
            new ConsultarCoberturaUseCase(new RequerirOperadorCoberturaUseCase(new CurrentUser(OperatorContext()), permissions), repository).EjecutarAsync(-56, -34));
        Assert.NotNull(permissions.Requested);
    }

    [Fact]
    public async Task QueryRejectsSuspendedOperatorBeforeConsultingTheRepository()
    {
        var permissions = new Permissions(true);
        var repository = new Zones();
        var suspended = OperatorContext() with { Estado = EstadoOrganizacion.Suspendida };

        await Assert.ThrowsAsync<AccesoCoberturaNoPermitidoException>(() =>
            new ConsultarCoberturaUseCase(new RequerirOperadorCoberturaUseCase(new CurrentUser(suspended), permissions), repository)
                .EjecutarAsync(-56, -34));
        Assert.Null(permissions.Requested);
    }

    private static ContextoUsuario OperatorContext()
    {
        var user = Guid.NewGuid();
        var access = Guid.NewGuid();
        return new(access, user, Guid.NewGuid(), null, "operator@example.invalid", "Operator", null, Guid.NewGuid(), "owner");
    }

    private static GeometryFactory GeometryFactory() => NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private sealed class CurrentUser(ContextoUsuario context) : IUsuarioActual
    {
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(context);
    }

    private sealed class Permissions(bool allowed) : IPermisosUsuarioRepository
    {
        public (Guid UserId, Guid AccessId, string Permission)? Requested { get; private set; }

        public Task<bool> TienePermisoAsync(Guid usuarioId, Guid accesoId, string codigoPermiso,
            CancellationToken cancellationToken = default)
        {
            Requested = (usuarioId, accesoId, codigoPermiso);
            return Task.FromResult(allowed);
        }

        public Task<IReadOnlyList<string>> ListarCodigosAsync(Guid usuarioId, Guid accesoId,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class Zones : IZonasCoberturaRepository
    {
        public ZonaCobertura? Created { get; private set; }
        public Guid? CreatedBy { get; private set; }

        public Task<(IReadOnlyList<ZonaCoberturaResumen> Items, int Total)> ListarAsync(
            EstadoZonaCobertura? estado, int pagina, int tamanoPagina, CancellationToken cancellationToken = default)
            => Task.FromResult<(IReadOnlyList<ZonaCoberturaResumen>, int)>(([], 0));

        public Task<ZonaCobertura?> ObtenerAsync(Guid zonaId, CancellationToken cancellationToken = default)
            => Task.FromResult<ZonaCobertura?>(null);

        public Task<IReadOnlyList<ZonaCobertura>> ConsultarCoberturaAsync(double longitud, double latitud,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ZonaCobertura>>([]);

        public Task<IReadOnlyList<DepartamentoReferenciaResumen>> ListarDepartamentosAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DepartamentoReferenciaResumen>>([]);

        public Task<DepartamentoReferencia?> ObtenerDepartamentoAsync(string codigo, CancellationToken cancellationToken = default)
            => Task.FromResult<DepartamentoReferencia?>(null);

        public Task GuardarNuevaAsync(ZonaCobertura zona, Guid usuarioId, CancellationToken cancellationToken = default)
        {
            Created = zona;
            CreatedBy = usuarioId;
            return Task.CompletedTask;
        }

        public Task GuardarCambioAsync(ZonaCobertura zona, long versionEsperada, Guid usuarioId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
