using System.Buffers.Binary;
using System.IO.Compression;
using NexoRuta.Infrastructure.Reparto;

namespace NexoRuta.UnitTests.Reparto;

public sealed class EvidenciaImagenValidatorTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
    private static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD50ooor8MP9Uz/2Q==");

    private static readonly byte[] ProgressiveJpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wgARCAACAAIDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAVAQEBAAAAAAAAAAAAAAAAAAAFBv/aAAwDAQACEAMQAAABigy4/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABAH/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPxB//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPxB//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxB//9k=");

    [Fact]
    public void AcceptsFullyDecodedPngAndJpeg()
    {
        EvidenciaImagenValidator.Validar(Png);
        EvidenciaImagenValidator.Validar(Jpeg);
        EvidenciaImagenValidator.Validar(ProgressiveJpeg);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("png-truncated")]
    [InlineData("jpeg-truncated")]
    [InlineData("jpeg-no-pixels")]
    [InlineData("crc")]
    [InlineData("dimension")]
    [InlineData("pixels")]
    [InlineData("bytes")]
    [InlineData("inflate")]
    [InlineData("pixels-truncated")]
    [InlineData("progressive-truncated")]
    [InlineData("jpeg-entropy-truncated")]
    public void RejectsInvalidOrUnboundedImages(string kind)
    {
        var bytes = kind switch
        {
            "header" => new byte[] { 137,80,78,71,13,10,26,10,0,0,0,0,0,0,0,0 },
            "png-truncated" => Png[..^12],
            "jpeg-truncated" => Jpeg[..^2],
            "jpeg-no-pixels" => Jpeg[..(Jpeg.Length / 2)].Concat(new byte[] { 255,217 }).ToArray(),
            "crc" => Png.Select((b, i) => i == 45 ? (byte)(b ^ 1) : b).ToArray(),
            "dimension" => WithDimensions(8193, 1),
            "pixels" => WithDimensions(8192, 8192),
            "bytes" => new byte[EvidenciaImagenValidator.MaximoBytes + 1],
            "inflate" => WithImageData(new byte[1024 * 1024]),
            "pixels-truncated" => WithImageData([0]),
            "progressive-truncated" => ProgressiveJpeg[..^5].Concat(new byte[] { 255, 217 }).ToArray(),
            "jpeg-entropy-truncated" => Jpeg[..^5].Concat(new byte[] { 255, 217 }).ToArray(),
            _ => throw new InvalidOperationException()
        };
        var error = Assert.Throws<ArgumentException>(() => EvidenciaImagenValidator.Validar(bytes));
        Assert.Contains("evidencia", error.Message);
        Assert.DoesNotContain("?", error.Message);
    }

    private static byte[] WithDimensions(int width, int height)
    {
        var bytes = Png.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29), Crc(bytes.AsSpan(12, 17)));
        return bytes;
    }

    private static byte[] WithImageData(byte[] pixels)
    {
        using var data = new MemoryStream();
        using (var compressor = new ZLibStream(data, CompressionLevel.SmallestSize, true))
            compressor.Write(pixels);
        using var png = new MemoryStream();
        png.Write(Png.AsSpan(0, 33));
        var payload = data.ToArray();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        png.Write(length); png.Write("IDAT"u8); png.Write(payload);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc("IDAT"u8.ToArray().Concat(payload).ToArray()));
        png.Write(length); png.Write(Png.AsSpan(Png.Length - 12));
        return png.ToArray();
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }
}
