using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.UnitTests.Administracion;

public sealed class GestionarRolesUseCaseTests
{
    [Fact]
    public async Task RolesAsignables_RequiresDynamicUserManagementPermission()
    {
        var permissions = new Permissions(true);
        var useCase = new GestionarRolesUseCase(new Actor(), permissions, new Roles());

        await useCase.ListarAsignablesAsync();

        Assert.Equal(new[] { "usuarios.gestionar", "configuracion.gestionar" }, permissions.Requests);
    }

    [Fact]
    public async Task CreateUsesActorOrganizationRatherThanRequestOwnership()
    {
        var actor = new Actor();
        var repository = new Roles();
        var permissions = new Permissions(true);
        await new GestionarRolesUseCase(actor, permissions, repository).CrearAsync("custom", "Custom");
        Assert.Equal(actor.Context.ComercioId, repository.Created!.ComercioId);
        Assert.Null(repository.Created.OperadorId);
        Assert.Equal(new[] { "configuracion.gestionar" }, permissions.Requests);
    }

    [Fact]
    public async Task PermissionDenialDoesNotReachRoleRepository()
    {
        var repository = new Roles();
        await Assert.ThrowsAsync<AccesoNoPermitidoException>(() =>
            new GestionarRolesUseCase(new Actor(), new Permissions(false), repository).CrearAsync("custom", "Custom"));
        Assert.Null(repository.Created);
    }

    [Fact]
    public async Task PermissionCatalogIsRequestedForTheAuthenticatedOrganization()
    {
        var actor = new Actor();
        var repository = new Roles();
        var permissions = new Permissions(true);

        await new GestionarRolesUseCase(actor, permissions, repository).PermisosAsync();

        Assert.Same(actor.Context, repository.PermissionCatalogActor);
        Assert.Equal(new[] { "configuracion.gestionar" }, permissions.Requests);
    }

    [Fact]
    public async Task AssignmentRequiresUserManagementAndScopedRepositoryAcceptance()
    {
        var permissions = new Permissions(true);
        var useCase = new GestionarRolesUseCase(new Actor(), permissions, new Roles());
        await Assert.ThrowsAsync<AccesoNoPermitidoException>(() => useCase.AsignarAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(new[] { "usuarios.gestionar", "configuracion.gestionar" }, permissions.Requests);
    }

    private sealed class Actor : IUsuarioActual
    {
        public ContextoUsuario Context { get; } = new(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
            "actor@test.local", null, "Commerce", Guid.NewGuid(), "custom");
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(Context);
    }
    private sealed class Permissions(bool allowed) : IPermisosUsuarioRepository
    {
        public List<string> Requests { get; } = [];
        public Task<bool> TienePermisoAsync(Guid usuarioId, Guid accesoId, string codigoPermiso, CancellationToken cancellationToken = default)
        { Requests.Add(codigoPermiso); return Task.FromResult(allowed); }
        public Task<IReadOnlyList<string>> ListarCodigosAsync(Guid usuarioId, Guid accesoId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> PuedeGestionarDecisionesOperativasEnvioAsync(Guid usuarioId, Guid accesoId, Guid rolId,
            Guid operadorId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
    private sealed class Roles : IRolesRepository
    {
        public Rol? Created { get; private set; }
        public ContextoUsuario? PermissionCatalogActor { get; private set; }
        public Task CrearAsync(Rol role, CancellationToken ct) { Created = role; return Task.CompletedTask; }
        public Task<bool> AsignarAsync(ContextoUsuario actor, Guid accesoId, Guid? rolId, bool puedeAsignarRolProtegido, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RenombrarAsync(ContextoUsuario actor, Guid id, string nombre, CancellationToken ct) => Task.FromResult(false);
        public Task<Rol?> ObtenerAsync(ContextoUsuario actor, Guid id, CancellationToken ct) => Task.FromResult<Rol?>(null);
        public Task<bool> EliminarAsync(ContextoUsuario actor, Guid id, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> ActualizarPermisosAsync(ContextoUsuario actor, Guid id, IReadOnlyList<string> codigos, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<RolResumen>> ListarAsync(ContextoUsuario actor, CancellationToken ct) => Task.FromResult<IReadOnlyList<RolResumen>>([]);
        public Task<IReadOnlyList<PermisoResumen>> PermisosAsync(ContextoUsuario actor, CancellationToken ct)
        { PermissionCatalogActor = actor; return Task.FromResult<IReadOnlyList<PermisoResumen>>([]); }
    }
}
