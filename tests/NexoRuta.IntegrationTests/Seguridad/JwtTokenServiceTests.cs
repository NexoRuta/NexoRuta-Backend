using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Results;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class JwtTokenServiceTests
{
    [Theory]
    [InlineData(null, false, null, 15)]
    [InlineData("240", false, null, 240)]
    [InlineData("240", true, 30, 15)]
    [InlineData("240", true, 5, 5)]
    public void JwtUsesTheConfiguredWebLifetimeAndKeepsMobileLimits(
        string? webMinutes, bool mobile, int? mobileDeadlineMinutes, int expectedMinutes)
    {
        var settings = AuthenticationTestData.Settings();
        if (webMinutes is not null)
            settings["Jwt:VigenciaWebMinutos"] = webMinutes;

        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var config = new JwtConfiguracion(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var issuer = new JwtTokenService(config, new FixedClock(now));
        var identity = new SesionIniciada(Guid.NewGuid(), Guid.NewGuid(), "comercio@example.test", 1);

        var (token, expiry) = issuer.Generar(identity, mobile ? Guid.NewGuid() : null,
            mobileDeadlineMinutes is { } minutes ? now.AddMinutes(minutes) : null);

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(now.UtcDateTime, parsed.ValidFrom);
        Assert.Equal(now.AddMinutes(expectedMinutes).UtcDateTime, expiry);
        Assert.Equal(expiry, parsed.ValidTo);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
