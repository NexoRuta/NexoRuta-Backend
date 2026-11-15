using Microsoft.Extensions.Configuration;

namespace NexoRuta.Infrastructure.Persistence;

public static class DemoActorPassword
{
    public const string ConfigurationKey = "DemoData:ActorPassword";

    public static string Requerir(IConfiguration configuration)
    {
        var password = configuration[ConfigurationKey];
        if (password is null || password.Length < 12 || password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException(
                "General demo data seeding requires a configured actor password of at least 12 characters without CR, LF, or NUL.");
        return password;
    }
}
