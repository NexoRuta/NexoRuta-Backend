using System.Text;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Features.Plataforma;

public static class InicializarPlataformaCommand
{
    public static async Task<int> EjecutarAsync(IServiceProvider services)
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
