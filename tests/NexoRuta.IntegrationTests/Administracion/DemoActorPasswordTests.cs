using Microsoft.Extensions.Configuration;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class DemoActorPasswordTests
{
    [Fact]
    public void MissingOrInvalidConfigurationIsRejectedWithoutEchoingTheValue()
    {
        string?[] invalidPasswords =
        {
            null,
            "",
            "short-password",
            "long-password-has\nline-break",
            "long-password-has\rreturn",
            "long-password-has\0null"
        };

        foreach (var password in invalidPasswords)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DemoActorPassword.ConfigurationKey] = password
            }).Build();

            var error = Assert.Throws<InvalidOperationException>(() => DemoActorPassword.Requerir(configuration));
            if (!string.IsNullOrEmpty(password))
                Assert.DoesNotContain(password, error.ToString());
        }
    }

    [Fact]
    public void ExternalPasswordIsReturnedWithoutTransformation()
    {
        const string password = "Synthetic-actor-password-2026";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DemoActorPassword.ConfigurationKey] = password
        }).Build();

        Assert.Equal(password, DemoActorPassword.Requerir(configuration));
    }
}
