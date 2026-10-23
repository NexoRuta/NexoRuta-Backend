using System.Net.Mail;
using System.Text.RegularExpressions;
using StbImageSharp;

namespace NexoRuta.Infrastructure.IdentidadVisual;

public sealed record IdentidadVisualNormalizada(
    string? NombreComercial,
    string? CorreoPublico,
    string? TelefonoPublico,
    string? ColorPrimario,
    string? ColorSecundario);

public sealed record LogoIdentidadVisual(byte[] Contenido, string TipoContenido, int Ancho, int Alto);

public static partial class ValidadorIdentidadVisual
{
    public const int MaximoNombre = 160;
    public const int MaximoCorreo = 254;
    public const int MaximoTelefono = 32;
    public const int MaximoLogoBytes = 1_048_576;
    public const int MaximoDimensionLogo = 2_048;
    public const int MaximoPixelesLogo = 4_194_304;

    public static IdentidadVisualNormalizada Normalizar(string? nombreComercial, string? correoPublico,
        string? telefonoPublico, string? colorPrimario, string? colorSecundario)
        => new(Nombre(nombreComercial), Correo(correoPublico), Telefono(telefonoPublico),
            Color(colorPrimario, nameof(colorPrimario)), Color(colorSecundario, nameof(colorSecundario)));

    public static LogoIdentidadVisual ValidarLogo(byte[] contenido)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        if (contenido.Length is 0 or > MaximoLogoBytes)
            throw new ArgumentException("El logo debe tener entre 1 byte y 1 MiB.", nameof(contenido));

        var tipo = TipoImagen(contenido);
        if (tipo is null)
            throw new ArgumentException("El logo debe ser una imagen PNG o JPEG estática válida.", nameof(contenido));

        try
        {
            if (tipo == "image/png" && !ChecksumsPngValidos(contenido))
                throw new ArgumentException("El logo PNG contiene una estructura o checksum inválido.", nameof(contenido));
            if (tipo == "image/png" && ContieneAnimacionPng(contenido))
                throw new ArgumentException("No se admiten logos PNG animados.", nameof(contenido));

            using var stream = new MemoryStream(contenido, writable: false);
            var info = ImageInfo.FromStream(stream)
                ?? throw new ArgumentException("El archivo no contiene una imagen válida.", nameof(contenido));
            if (info.Width is < 1 or > MaximoDimensionLogo || info.Height is < 1 or > MaximoDimensionLogo
                || (long)info.Width * info.Height > MaximoPixelesLogo)
                throw new ArgumentException("El logo no puede superar 2048 × 2048 píxeles.", nameof(contenido));

            // Header-only parsing is insufficient: decode every pixel before persisting untrusted bytes.
            stream.Position = 0;
            _ = ImageResult.FromStream(stream);
            return new LogoIdentidadVisual(contenido.ToArray(), tipo, info.Width, info.Height);
        }
        catch (ArgumentException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ArgumentException("El archivo no contiene una imagen PNG o JPEG válida.", nameof(contenido), exception);
        }
    }

    private static string? Nombre(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized is not null && (normalized.Length > MaximoNombre || normalized.Any(char.IsControl)))
            throw new ArgumentException("El nombre comercial no puede superar los 160 caracteres.", nameof(value));
        return normalized;
    }

    private static string? Correo(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized is null) return null;
        if (normalized.Length > MaximoCorreo || normalized.Any(char.IsWhiteSpace)
            || normalized.Count(character => character == '@') != 1
            || !MailAddress.TryCreate(normalized, out var address)
            || address is null || address.DisplayName.Length != 0
            || !string.Equals(address.Address, normalized, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ingresá un correo válido de hasta 254 caracteres, sin nombre para mostrar.", nameof(value));
        return normalized;
    }

    private static string? Telefono(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized is null) return null;
        var digits = normalized.Count(character => character is >= '0' and <= '9');
        var depth = 0;
        var parenthesesAreBalanced = true;
        foreach (var character in normalized)
        {
            if (character == '(')
            {
                if (depth != 0) parenthesesAreBalanced = false;
                depth++;
            }
            else if (character == ')')
            {
                if (depth != 1) parenthesesAreBalanced = false;
                depth--;
            }
        }
        parenthesesAreBalanced &= depth == 0;
        if (normalized.Length > MaximoTelefono || !TelefonoPattern().IsMatch(normalized)
            || digits is < 7 or > 15 || normalized.Count(character => character == '+') > 1
            || (normalized.Contains('+') && normalized[0] != '+')
            || !parenthesesAreBalanced)
            throw new ArgumentException("Ingresá un teléfono con 7 a 15 dígitos; se admiten +, espacios, paréntesis, puntos y guiones.", nameof(value));
        return normalized;
    }

    private static string? Color(string? value, string parameter)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized is null) return null;
        if (!Regex.IsMatch(normalized, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("El color debe usar el formato #RRGGBB.", parameter);
        return normalized.ToUpperInvariant();
    }

    private static string? TipoImagen(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.StartsWith(png)) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return "image/jpeg";
        return null;
    }

    private static bool ContieneAnimacionPng(ReadOnlySpan<byte> bytes)
    {
        var offset = 8;
        while (offset <= bytes.Length - 12)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > bytes.Length) return false;
            if (bytes.Slice(offset + 4, 4).SequenceEqual("acTL"u8)) return true;
            offset += checked((int)length + 12);
        }
        return false;
    }

    private static bool ChecksumsPngValidos(ReadOnlySpan<byte> bytes)
    {
        var offset = 8;
        var tieneCabecera = false;
        while (offset <= bytes.Length - 12)
        {
            var longitud = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (longitud > int.MaxValue || offset + 12L + longitud > bytes.Length) return false;
            var tipo = bytes.Slice(offset + 4, 4);
            if (!tieneCabecera && !tipo.SequenceEqual("IHDR"u8)) return false;
            if (tipo.SequenceEqual("IHDR"u8))
            {
                if (tieneCabecera || longitud != 13) return false;
                tieneCabecera = true;
            }

            var datosConTipo = bytes.Slice(offset + 4, checked((int)longitud + 4));
            var checksum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                bytes.Slice(offset + 8 + (int)longitud, 4));
            if (checksum != CalcularCrc32(datosConTipo)) return false;

            offset += checked((int)longitud + 12);
            if (tipo.SequenceEqual("IEND"u8))
                return longitud == 0 && tieneCabecera && offset == bytes.Length;
        }
        return false;
    }

    private static uint CalcularCrc32(ReadOnlySpan<byte> bytes)
    {
        var checksum = uint.MaxValue;
        foreach (var valor in bytes)
        {
            checksum ^= valor;
            for (var bit = 0; bit < 8; bit++)
                checksum = (checksum & 1) != 0 ? (checksum >> 1) ^ 0xedb88320u : checksum >> 1;
        }
        return ~checksum;
    }

    [GeneratedRegex("^\\+?[0-9(). -]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TelefonoPattern();
}
