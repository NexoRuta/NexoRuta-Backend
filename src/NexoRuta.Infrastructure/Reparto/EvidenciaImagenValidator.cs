using BitMiracle.LibJpeg.Classic;
using NexoRuta.Infrastructure.IdentidadVisual;

namespace NexoRuta.Infrastructure.Reparto;

public static class EvidenciaImagenValidator
{
    public const int MaximoBytes = 2 * 1024 * 1024;
    public const int MaximoDimension = 8192;
    public const int MaximoPixeles = 16 * 1024 * 1024;

    public static void Validar(byte[] contenido)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        if (contenido.Length is < 16 or > MaximoBytes)
            throw new ArgumentException("La evidencia debe ser PNG/JPEG de hasta 2 MiB.", nameof(contenido));
        if (contenido.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
            ValidarJpeg(contenido);
        else
        {
            // Bound dimensions before decoding and actual PNG inflation, not just declared pixel size.
            try { _ = ValidadorIdentidadVisual.ValidarImagen(contenido, MaximoDimension, MaximoPixeles); }
            catch (ArgumentException error)
            {
                throw new ArgumentException("La evidencia no contiene una imagen PNG/JPEG v\u00e1lida dentro de los l\u00edmites permitidos.", nameof(contenido), error);
            }
        }
    }

    private static void ValidarJpeg(byte[] contenido)
    {
        if (!contenido.AsSpan().EndsWith(new byte[] { 255, 217 }))
            throw new ArgumentException("La evidencia JPEG est\u00e1 truncada.", nameof(contenido));
        var decoder = new jpeg_decompress_struct(new ErroresJpegEstrictos());
        decoder.Progress = new jpeg_progress_mgr();
        decoder.Progress.OnProgress += (_, _) =>
        {
            // Bound work from progressive scans as well as encoded bytes and pixel allocations.
            if (decoder.Input_scan_number > 100)
                throw new ArgumentException("La evidencia JPEG supera los 100 barridos permitidos.");
        };
        try
        {
            using var stream = new MemoryStream(contenido, writable: false);
            decoder.jpeg_stdio_src(stream);
            if (decoder.jpeg_read_header(true) != ReadResult.JPEG_HEADER_OK
                || decoder.Image_width is < 1 or > MaximoDimension || decoder.Image_height is < 1 or > MaximoDimension
                || (long)decoder.Image_width * decoder.Image_height > MaximoPixeles || decoder.Num_components is < 1 or > 4)
                throw new ArgumentException("La evidencia JPEG supera las dimensiones o cantidad de p\u00edxeles permitidas.", nameof(contenido));
            if (!decoder.jpeg_start_decompress()) throw new ArgumentException("La evidencia JPEG est\u00e1 incompleta.", nameof(contenido));
            // Decode every scanline without retaining a second full-resolution image buffer.
            byte[][] row = [new byte[checked(decoder.Output_width * decoder.Output_components)]];
            while (decoder.Output_scanline < decoder.Output_height)
                if (decoder.jpeg_read_scanlines(row, 1) != 1)
                    throw new ArgumentException("La evidencia JPEG est\u00e1 incompleta.", nameof(contenido));
            if (!decoder.jpeg_finish_decompress()) throw new ArgumentException("La evidencia JPEG est\u00e1 incompleta.", nameof(contenido));
        }
        catch (ArgumentException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new ArgumentException("La evidencia JPEG no contiene una imagen v\u00e1lida.", nameof(contenido), error);
        }
        finally { decoder.jpeg_destroy(); }
    }

    private sealed class ErroresJpegEstrictos : jpeg_error_mgr
    {
        public override void error_exit() => throw new ArgumentException("La evidencia JPEG no contiene una imagen v\u00e1lida.");
        // Never recover missing entropy/pixels by silently manufacturing replacement pixels.
        public override void emit_message(int level)
        {
            if (level < 0) error_exit();
        }
    }
}
