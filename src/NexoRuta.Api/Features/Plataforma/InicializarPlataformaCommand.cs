using System.Text;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Features.Plataforma;

public static class InicializarPlataformaCommand
{
    private const string CorreoAdminDemo = "admin-demo@nexoruta.test";

    public static Task<int> EjecutarAsync(IServiceProvider services, string operation)
        => operation == "sync-demo-password"
            ? SincronizarPasswordAdminDemoAsync(services)
            : CrearAsync(services);

    private static async Task<int> CrearAsync(IServiceProvider services)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("An interactive terminal is required; credentials are never accepted as arguments.");
            return 2;
        }
        Console.WriteLine("Initialize the first platform administrator on the configured database. No migrations or demo seeders will run.");
        Console.Write("Email: ");
        var email = Console.ReadLine() ?? "";
        Console.Write("Password (hidden): ");
        var password = ReadPassword();
        Console.Write("Confirm password (hidden): ");
        if (password != ReadPassword())
        {
            Console.Error.WriteLine("Passwords do not match; nothing was changed.");
            return 2;
        }
        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPlataformaRepository>().InicializarAsync(email, password);
            Console.WriteLine("Platform administrator created. No session or token was issued.");
            return 0;
        }
        catch (CorreoYaRegistradoException)
        {
            Console.Error.WriteLine("Email already registered; no account was changed.");
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("A valid email and a password of at least 12 characters are required; nothing was changed.");
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine("Initialization refused; verify whether platform initialization or the account already exists. Nothing was changed.");
        }
        catch (Exception)
        {
            // Never print credential-bearing configuration or provider exception details.
            Console.Error.WriteLine("Initialization failed; no partial initialization was committed. Check database availability and applied migrations.");
        }
        return 1;
    }

    private static async Task<int> SincronizarPasswordAdminDemoAsync(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var email = configuration["PlatformInitialization:Email"];
        var password = configuration["PlatformInitialization:Password"];
        if (email is null || !string.Equals(email, CorreoAdminDemo, StringComparison.OrdinalIgnoreCase)
            || password is null || password.Length < 12
            || password.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            Console.Error.WriteLine("Configured demo administrator credentials are missing or invalid (minimum 12 characters); no account was changed.");
            return 2;
        }

        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPlataformaRepository>()
                .SincronizarPasswordExistenteAsync(email, password);
            Console.WriteLine("Existing demo platform administrator synchronized. Other identities and permissions were not changed.");
            return 0;
        }
        catch (CorreoYaRegistradoException)
        {
            Console.Error.WriteLine("Synchronization refused because the configured email belongs to another identity; nothing was changed.");
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine("Synchronization refused because the existing platform identity is absent or incompatible; nothing was changed.");
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Configured demo administrator credentials are invalid; no account was changed.");
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Synchronization failed; no partial change was committed. Check database availability and applied migrations.");
        }
        return 1;
    }

    private static string ReadPassword()
    {
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
