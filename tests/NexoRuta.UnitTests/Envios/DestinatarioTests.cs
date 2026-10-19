using NexoRuta.Domain.Envios;
namespace NexoRuta.UnitTests.Envios;

public sealed class DestinatarioTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-correo")]
    [InlineData("Nombre <persona@example.test>")]
    [InlineData("persona@example.test\r\nBcc: otra@example.test")]
    public void CorreoInvalidoNoPermiteCrearDestinatario(string email) =>
        Assert.Throws<ArgumentException>(() => new Destinatario(Guid.CreateVersion7(), "Destinatario", email));

    [Fact]
    public void CorreoValidoSeConservaSinAlterarSuParteLocal()
    {
        var destinatario = new Destinatario(Guid.CreateVersion7(), "Destinatario", " Persona+pedidos@example.test ");
        Assert.Equal("Persona+pedidos@example.test", destinatario.Email);
    }
}
