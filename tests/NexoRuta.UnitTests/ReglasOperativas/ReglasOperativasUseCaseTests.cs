using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Application.ReglasOperativas.UseCases;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.UnitTests.ReglasOperativas;

public sealed class ReglasOperativasUseCaseTests
{
    [Fact]
    public async Task QueryUsesPersistedOperatorAndRejectsCommerceEvenWithSharedPermission()
    {
        var operatorId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var accessId = Guid.NewGuid();
        var permissions = new Permissions(true);
        var repository = new Repository();
        var operatorContext = new ContextoUsuario(accessId, userId, operatorId, null,
            "operator@example.test", "Operator", null, Guid.NewGuid(), "admin");
        var query = new ConsultarReglasOperativasUseCase(Guard(operatorContext, permissions), repository);

        var result = await query.EjecutarAsync();

        Assert.Null(result.Operador);
        Assert.Equal(operatorId, repository.LastOperatorId);
        Assert.Equal(CatalogoPermisos.ConfiguracionGestionar, permissions.LastPermission);

        var commerceContext = new ContextoUsuario(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
            "commerce@example.test", null, "Commerce", Guid.NewGuid(), "owner");
        var commerce = new ConsultarReglasOperativasUseCase(Guard(commerceContext, new Permissions(true)), repository);
        await Assert.ThrowsAsync<AccesoCoberturaNoPermitidoException>(() => commerce.EjecutarAsync());
        Assert.Equal(operatorId, repository.LastOperatorId);
    }

    private static RequerirOperadorCoberturaUseCase Guard(ContextoUsuario actor, Permissions permissions)
        => new(new CurrentUser(actor), permissions);

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

    private sealed class Repository : IReglasOperativasRepository
    {
        public Guid? LastOperatorId { get; private set; }
        public Task<ReglasOperativasConsulta> ConsultarAsync(Guid operadorId, CancellationToken cancellationToken = default)
        {
            LastOperatorId = operadorId;
            return Task.FromResult(new ReglasOperativasConsulta(null, null, null));
        }
        public Task<(IReadOnlyList<VersionReglasOperativas> Items, int Total)> ListarPublicacionesAsync(
            Guid operadorId, int pagina, int tamanoPagina, CancellationToken cancellationToken = default)
            => Task.FromResult<(IReadOnlyList<VersionReglasOperativas> Items, int Total)>(([], 0));
        public Task<VersionReglasOperativas?> ObtenerPublicacionAsync(Guid operadorId, Guid versionId,
            CancellationToken cancellationToken = default) => Task.FromResult<VersionReglasOperativas?>(null);
        public Task<VersionReglasOperativas> CrearBorradorAsync(Guid operadorId, Guid usuarioId,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<VersionReglasOperativas> EditarBorradorAsync(Guid operadorId, Guid usuarioId, long versionEsperada,
            ReglasOperativasConfiguracion configuracion, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<VersionReglasOperativas> PublicarBorradorAsync(Guid operadorId, Guid usuarioId, long versionEsperada,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
