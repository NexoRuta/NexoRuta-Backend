using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.UnitTests.Administracion;

public sealed class EsPropietarioOrganizacionUseCaseTests
{
    [Fact]
    public async Task ChecksPersistedOwnershipForTheAuthenticatedAccessRatherThanRoleName()
    {
        var user = Guid.NewGuid();
        var access = Guid.NewGuid();
        var repository = new Accesses(user, access, true);
        var actor = new Actor(new(access, user, null, Guid.NewGuid(), "user@test.local", null, "Commerce", Guid.NewGuid(), "empleado"));

        Assert.True(await new EsPropietarioOrganizacionUseCase(actor, repository).EjecutarAsync());
        Assert.Equal((user, access), (repository.QueriedUserId, repository.QueriedAccessId));
    }

    [Fact]
    public async Task ASessionCannotCheckOwnershipForAnotherIdentityOrAccess()
    {
        var actor = new Actor(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            "operator@test.local", "Operator", null, Guid.NewGuid(), "owner"));
        var repository = new Accesses(Guid.NewGuid(), Guid.NewGuid(), true);

        Assert.False(await new EsPropietarioOrganizacionUseCase(actor, repository).EjecutarAsync());
    }

    private sealed class Actor(ContextoUsuario context) : IUsuarioActual
    {
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }

    private sealed class Accesses(Guid ownerUserId, Guid ownerAccessId, bool owner) : IAccesosUsuarioRepository
    {
        public Guid QueriedUserId { get; private set; }
        public Guid QueriedAccessId { get; private set; }

        public Task<bool> EsPropietarioAsync(Guid usuarioId, Guid accesoId, CancellationToken cancellationToken = default)
        {
            QueriedUserId = usuarioId;
            QueriedAccessId = accesoId;
            return Task.FromResult(owner && ownerUserId == usuarioId && ownerAccessId == accesoId);
        }

        public Task<IReadOnlyList<ContextoUsuario>> ListarAsync(TipoAccesoUsuario tipo, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextoUsuario>>([]);

        public Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default)
            => Task.FromResult<ContextoUsuario?>(null);
    }
}
