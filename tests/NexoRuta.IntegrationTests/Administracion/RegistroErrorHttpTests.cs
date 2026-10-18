using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Api.Controllers;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class RegistroErrorHttpTests
{
    [Theory]
    [InlineData("duplicate", HttpStatusCode.Conflict)]
    [InlineData("identity", HttpStatusCode.BadRequest)]
    [InlineData("email", HttpStatusCode.BadRequest)]
    [InlineData("password", HttpStatusCode.BadRequest)]
    [InlineData("success", HttpStatusCode.Created)]
    public async Task RegistrationPreservesStatusAndVisibleMessage(string scenario, HttpStatusCode status)
    {
        Exception? failure = scenario == "duplicate" ? new CorreoYaRegistradoException() : null;
        if (scenario == "identity")
            failure = new CredencialesInvalidasException();
        var repository = new RegistrationRepository(failure);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly).AddControllersAsServices();
        builder.Services.AddSingleton(new AuthController(null!, null!, repository));
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("auth-register", limiter =>
        {
            limiter.PermitLimit = 10;
            limiter.Window = TimeSpan.FromMinutes(1);
        }));
        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = scenario == "email" ? "bad" : "USER@example.test",
            Password = scenario == "password" ? "            " : "A-password-2026!"
        });
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(scenario == "password" ? 0 : 1, repository.Attempts);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (scenario == "password")
        {
            Assert.True(body.GetProperty("errors").TryGetProperty("Password", out _));
            return;
        }
        if (scenario == "success")
        {
            Assert.Equal("/api/usuarios/actual", response.Headers.Location?.ToString());
            Assert.Equal("user@example.test", body.GetProperty("email").GetString());
            return;
        }
        var expected = scenario switch
        {
            "duplicate" => "El correo ya está registrado.",
            "identity" => new ArgumentException("La cuenta no cumple las reglas de Identity.", "password").Message,
            "email" => new ArgumentException("El correo electrónico no es válido.", "email").Message,
            _ => new ArgumentException("La contraseña debe tener al menos 12 caracteres.", "password").Message
        };
        Assert.Equal(expected, body.GetProperty("message").GetString());
    }

    private sealed class RegistrationRepository(Exception? failure) : IRegistroRepository
    {
        public int Attempts { get; private set; }
        public Task<IdentidadSinOrganizacion> RegistrarAsync(string email, string password, CancellationToken ct)
        {
            Attempts++;
            if (failure is not null) throw failure;
            return Task.FromResult(new IdentidadSinOrganizacion(Guid.NewGuid(), ValidacionCredenciales.Validar(email, password)));
        }
        public Task<IdentidadSinOrganizacion?> ObtenerSinOrganizacionAsync(Guid usuarioId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
