using NexoRuta.Infrastructure.IdentidadVisual;

namespace NexoRuta.UnitTests.Administracion;

public sealed class IdentidadVisualValidatorTests
{
    [Fact]
    public void Normalize_optional_fields_trims_values_and_preserves_brand_accents()
    {
        var normalized = ValidadorIdentidadVisual.Normalizar(
            "  Ruta Sur  ", "  contacto@rutasur.uy ", "+598 99 123 456", "#aabbcc", "#102030");

        Assert.Equal("Ruta Sur", normalized.NombreComercial);
        Assert.Equal("contacto@rutasur.uy", normalized.CorreoPublico);
        Assert.Equal("+598 99 123 456", normalized.TelefonoPublico);
        Assert.Equal("#AABBCC", normalized.ColorPrimario);
        Assert.Equal("#102030", normalized.ColorSecundario);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Operator <contacto@rutasur.uy>")]
    [InlineData("a@b.uy\nmalicious")]
    public void Rejects_invalid_or_display_name_email(string email)
        => Assert.Throws<ArgumentException>(() => ValidadorIdentidadVisual.Normalizar(null, email, null, null, null));

    [Theory]
    [InlineData("abc123")]
    [InlineData("+598 12")]
    [InlineData("++59899123456")]
    [InlineData("+598 (99 123-456")]
    [InlineData(")(5989912)(3456")]
    public void Rejects_invalid_phone(string phone)
        => Assert.Throws<ArgumentException>(() => ValidadorIdentidadVisual.Normalizar(null, null, phone, null, null));

    [Fact]
    public void Rejects_non_hex_color_without_losing_accessible_state_tokens()
        => Assert.Throws<ArgumentException>(() => ValidadorIdentidadVisual.Normalizar(null, null, null, "red", null));

    [Fact]
    public void Rejects_arbitrary_bytes_as_logo()
        => Assert.Throws<ArgumentException>(() => ValidadorIdentidadVisual.ValidarLogo([1, 2, 3, 4, 5]));

    [Fact]
    public void Rejects_png_with_invalid_chunk_checksum()
        => Assert.Throws<ArgumentException>(() => ValidadorIdentidadVisual.ValidarLogo(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/S3sAAAAASUVORK5CYII=")));

    [Fact]
    public void Accepts_a_fully_decoded_png_with_supported_dimensions()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

        var logo = ValidadorIdentidadVisual.ValidarLogo(png);

        Assert.Equal("image/png", logo.TipoContenido);
        Assert.Equal(1, logo.Ancho);
        Assert.Equal(1, logo.Alto);
        Assert.Equal(png, logo.Contenido);
    }

    [Fact]
    public void Accepts_a_second_png_for_logo_replacement()
        => Assert.Equal("image/png", ValidadorIdentidadVisual.ValidarLogo(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPj/HwADAgH/5ncLrgAAAABJRU5ErkJggg==")).TipoContenido);
}
