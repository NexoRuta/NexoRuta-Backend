using System.Diagnostics;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class PlataformaCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandRejectsArgumentsOrRedirectedCredentialsWithoutConnectingOrRunningSeeders(bool invalidArguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("platform-admin");
        info.ArgumentList.Add("create");
        if (invalidArguments) info.ArgumentList.Add("--password");
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["ConnectionStrings__Postgres"] = "Host=127.0.0.1;Port=1;Database=unreachable;Username=unused;Timeout=1";
        info.Environment["DatabaseInitialization__ApplyMigrations"] = "true";
        info.Environment["DatabaseInitialization__SeedData"] = "true";
        info.Environment["PlatformInitialization__Enabled"] = "true";
        info.Environment.Remove("Jwt__SigningKeyBase64");
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, process.ExitCode);
        Assert.Contains(invalidArguments ? "Usage:" : "interactive terminal", await error);
        Assert.DoesNotContain("password=", await output, StringComparison.OrdinalIgnoreCase);
    }
}
