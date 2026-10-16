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
        var user = new UsuarioLogin(Guid.NewGuid(), Guid.NewGuid(), "user@example.test", "stored-hash", 7);
        var repository = new Users(user);
        var verifier = new PasswordVerifier(valid);
        var result = await new IniciarSesionUseCase(repository, verifier)
            .EjecutarAsync(" user@example.test ", " password-with-spaces ");
        Assert.Equal("user@example.test", repository.Email);
        Assert.Equal((user.Email, user.PasswordHash, " password-with-spaces "), verifier.Input);
        Assert.Equal(1, verifier.Calls);
        if (valid)
        {
            Assert.NotNull(result);
            Assert.Equal(user.UsuarioId, result.UsuarioId);
            Assert.Equal(user.AccesoId, result.AccesoId);
            Assert.Equal(user.Email, result.Email);
            Assert.Equal(user.VersionSesion, result.VersionSesion);
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

    private sealed class PasswordVerifier(bool valid) : IVerificadorPassword
    {
        public int Calls { get; private set; }
        public (string Email, string Hash, string Password)? Input { get; private set; }
        public bool Verificar(string email, string passwordHash, string password)
        {
            Calls++; Input = (email, passwordHash, password);
            return valid;
        }
    }
}
