using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Administracion.UseCases;

namespace NexoRuta.UnitTests.Administracion;

public sealed class IniciarSesionUseCaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginChecksPasswordAndReturnsOnlyCurrentIdentity(bool valid)
    {
        var user = new UsuarioLogin(Guid.NewGuid(), Guid.NewGuid(), "user@example.test", 7);
        var repository = new Users(user);
        var verifier = new PasswordVerifier(valid, 4);
        var result = await new IniciarSesionUseCase(repository, verifier)
            .EjecutarAsync(" user@example.test ", " password-with-spaces ");
        Assert.Equal("user@example.test", repository.Email);
        Assert.Equal((user.UsuarioId, " password-with-spaces "), verifier.Input);
        Assert.Equal(1, verifier.Calls);
        if (valid)
        {
            Assert.NotNull(result);
            Assert.Equal(user.UsuarioId, result.UsuarioId);
            Assert.Equal(user.AccesoId, result.AccesoId);
            Assert.Equal(user.Email, result.Email);
            Assert.Equal(user.VersionSesion, result.VersionSesion);
            Assert.Equal(4, result.VersionCredencial);
        }
        else Assert.Null(result);
    }

    [Fact]
    public async Task MissingUserDoesNotVerifyPassword()
    {
        var verifier = new PasswordVerifier(true);
        Assert.Null(await new IniciarSesionUseCase(new Users(null), verifier)
            .EjecutarAsync("missing@example.test", "password"));
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(null, "password")]
    [InlineData("", "password")]
    [InlineData(" ", "password")]
    [InlineData("user@example.test", null)]
    [InlineData("user@example.test", "")]
    [InlineData("user@example.test", " ")]
    public async Task InvalidInputDoesNotQueryOrVerify(string? email, string? password)
    {
        var repository = new Users(null);
        var verifier = new PasswordVerifier(true);
        Assert.Null(await new IniciarSesionUseCase(repository, verifier).EjecutarAsync(email!, password!));
        Assert.Equal(0, repository.Calls);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task CancellationIsPassedToRepositoryAndNotSwallowed()
    {
        var repository = new Users(null);
        var verifier = new PasswordVerifier(true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new IniciarSesionUseCase(repository, verifier)
                .EjecutarAsync("user@example.test", "password", cancellation.Token));
        Assert.Equal(cancellation.Token, repository.Cancellation);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task LoginWithoutMembershipHasExplicitIdentityOnlyState()
    {
        var user = new UsuarioLogin(Guid.NewGuid(), null, "person@example.test", 0);
        var session = await new IniciarSesionUseCase(new Users(user), new PasswordVerifier(true))
            .EjecutarAsync(user.Email, "valid-password");
        Assert.NotNull(session);
        Assert.Equal(user.UsuarioId, session.UsuarioId);
        Assert.Null(session.AccesoId);
        Assert.True(session.SinOrganizacion);
        Assert.False(session.EsPlataforma);
    }

    private sealed class Users(UsuarioLogin? user) : IUsuariosLoginRepository
    {
        public string? Email { get; private set; }
        public int Calls { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public Task<UsuarioLogin?> ObtenerPorEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            Email = email; Calls++; Cancellation = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(user);
        }
    }

    private sealed class PasswordVerifier(bool valid, long version = 0) : IVerificadorPassword
    {
        public int Calls { get; private set; }
        public (Guid UsuarioId, string Password)? Input { get; private set; }
        public Task<long?> VerificarYObtenerVersionCredencialAsync(Guid usuarioId, string password, CancellationToken cancellationToken = default)
        {
            Calls++; Input = (usuarioId, password);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(valid ? version : (long?)null);
        }
    }
}
