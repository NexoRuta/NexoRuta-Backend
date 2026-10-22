using NexoRuta.Worker.Messaging;

namespace NexoRuta.UnitTests.Messaging;

public sealed class PasswordRecoveryEmailTemplateTests
{
    [Fact]
    public void TemplateKeepsTheLinkInTheHtmlButtonAndShowsItsActualUtcExpiry()
    {
        const string link = "http://localhost:5102/restablecer-contrasena?user=sample-user&code=sample-value";
        var expiresAt = new DateTimeOffset(2026, 10, 22, 18, 45, 0, TimeSpan.Zero);

        var email = PasswordRecoveryEmailTemplate.Create(link, expiresAt);

        Assert.Contains("href=\"http://localhost:5102/restablecer-contrasena?user=sample-user&amp;code=sample-value\"", email.Html);
        Assert.Contains(">Crear nueva contraseña</a>", email.Html);
        Assert.Contains("Portal de comercios", email.Html);
        Assert.DoesNotContain(link, email.Html);
        Assert.Contains("22/10/2026 18:45 UTC", email.Html);
        Assert.Contains("22/10/2026 18:45 UTC", email.Text);
        Assert.Contains(link, email.Text);
        Assert.Contains("Crear nueva contraseña", email.Text);
    }
}
