using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.UnitTests.Administracion;

public sealed class RegistroErrorContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateMappingUsesTheConfiguredIdentityDescriber(bool custom)
    {
        using var services = Services(custom);
        using var scope = services.CreateScope();
        var describer = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>().ErrorDescriber;
        if (!custom)
        {
            Assert.IsType<IdentityErrorDescriber>(describer);
            Assert.Equal(nameof(IdentityErrorDescriber.DuplicateEmail), describer.DuplicateEmail("user@example.test").Code);
            Assert.Equal(nameof(IdentityErrorDescriber.DuplicateUserName), describer.DuplicateUserName("user@example.test").Code);
        }
        else Assert.IsType<CustomDescriber>(describer);

        foreach (var error in new[] { describer.DuplicateEmail("user@example.test"), describer.DuplicateUserName("user@example.test") })
        {
            var failure = IdentityResult.Failed(error);
            Assert.True(IdentityErrorContract.EsDuplicado(failure, describer));
            Assert.IsType<CorreoYaRegistradoException>(CreationError(failure, describer));
        }
        Assert.False(IdentityErrorContract.EsDuplicado(IdentityResult.Success, describer));
        Assert.False(IdentityErrorContract.EsDuplicado(IdentityResult.Failed(describer.InvalidEmail("bad")), describer));
        if (custom)
            Assert.False(IdentityErrorContract.EsDuplicado(IdentityResult.Failed(new IdentityError { Code = "DuplicateEmail" }), describer));
    }

    [Fact]
    public void NonDuplicateCreationFailureHasAnApplicationTypeAndSuccessDoesNotThrow()
    {
        var describer = new IdentityErrorDescriber();
        var failure = CreationError(IdentityResult.Failed(describer.PasswordTooShort(12)), describer);
        Assert.IsType<CredencialesInvalidasException>(failure);
        Assert.IsAssignableFrom<ArgumentException>(failure);
        Assert.Null(CreationError(IdentityResult.Success, describer));
        Assert.IsType<CorreoYaRegistradoException>(CreationError(IdentityResult.Failed(
            describer.PasswordTooShort(12), describer.DuplicateEmail("user@example.test")), describer));
    }

    private static ServiceProvider Services(bool custom)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexoRutaPersistence(configuration);
        if (custom) services.AddIdentityCore<Usuario>().AddErrorDescriber<CustomDescriber>();
        return services.BuildServiceProvider();
    }

    private static Exception? CreationError(IdentityResult result, IdentityErrorDescriber describer)
        => Record.Exception(() => IdentityErrorContract.ExigirCreacionValida(result, describer));

    public sealed class CustomDescriber : IdentityErrorDescriber
    {
        public override IdentityError DuplicateEmail(string email) => new() { Code = "email-already-used" };
        public override IdentityError DuplicateUserName(string userName) => new() { Code = "username-already-used" };
    }
}
