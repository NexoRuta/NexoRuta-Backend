using System.Globalization;
using System.Text.RegularExpressions;
using NexoRuta.Application.Envios.Results;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace NexoRuta.Api.Features.Envios.Etiquetas;

public static class EtiquetasEnvioPdf
{
    private const decimal Mm = 72m / 25.4m;
    private const decimal Width = 100 * Mm, Height = 150 * Mm, Margin = 5 * Mm;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-UY");

    public static byte[] Generar(EnvioDetalle envio)
    {
        var bultos = envio.Bultos.OrderBy(b => b.Codigo, StringComparer.Ordinal).ThenBy(b => b.Id).ToArray();
        if (bultos.Length == 0 || bultos.Any(b => b.Id == Guid.Empty) || bultos.Select(b => b.Id).Distinct().Count() != bultos.Length)
            throw new InvalidDataException("Los bultos requieren identificadores persistidos únicos.");
        using var builder = new PdfDocumentBuilder();
        using var source = typeof(EtiquetasEnvioPdf).Assembly.GetManifestResourceStream(
            "NexoRuta.Api.Features.Envios.Etiquetas.Fonts.OpenSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream(); source.CopyTo(fontBytes);
        var font = builder.AddTrueTypeFont(fontBytes.ToArray());
        for (var index = 0; index < bultos.Length; index++)
        {
            var bulto = bultos[index];
            var page = builder.AddPage((double)Width, (double)Height);
            const decimal qrSize = 33 * Mm;
            var fields = new[]
            {
                ("Destinatario", envio.DestinatarioNombre), ("Dirección", envio.Direccion),
                ("Remitente", envio.Origen.ComercioNombre), ("Operador", envio.Origen.OperadorNombre),
                ("Peso", Format(bulto.PesoGramos) + " g"),
                ("Dimensiones", $"{Format(bulto.LargoCentimetros)} × {Format(bulto.AnchoCentimetros)} × {Format(bulto.AltoCentimetros)} cm")
            };
            var codeLines = Wrap(bulto.Codigo, 12);
            var bodyStart = Height - Margin - 10 - 20 - codeLines.Count * 15 - 3 - qrSize - 12 - 12 - 15;
            decimal size = 10;
            while (size > 8 && RequiredHeight(size) > bodyStart - Margin) size -= .5m;
            // Prefer 100x150; extend only exceptional text instead of clipping it or making it unreadably small.
            var extra = Math.Max(0, RequiredHeight(size) - (bodyStart - Margin));
            page.PageSize = new PdfRectangle(0, 0, (double)Width, (double)(Height + extra));
            var y = Height + extra - Margin - 10;
            Text("NEXORUTA", 10, Margin, y);
            Text($"Bulto {index + 1} de {bultos.Length}", 11, Width - Margin - 95, y);
            y -= 20;
            foreach (var line in codeLines) { Text(line, 12, Margin, y); y -= 15; }
            y -= 3;
            var matrix = new QRCodeWriter().encode(bulto.Id.ToString("D"), BarcodeFormat.QR_CODE, 0, 0,
                new Dictionary<EncodeHintType, object> { [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M, [EncodeHintType.MARGIN] = 4 });
            var module = qrSize / matrix.Width;
            var left = (Width - qrSize) / 2;
            var bottom = y - qrSize;
            for (var row = 0; row < matrix.Height; row++)
                for (var col = 0; col < matrix.Width;)
                {
                    if (!matrix[col, row]) { col++; continue; }
                    var start = col;
                    while (col < matrix.Width && matrix[col, row]) col++;
                    page.DrawRectangle(P(left + start * module, bottom + (matrix.Height - row - 1) * module),
                        (col - start) * module, module, 0, fill: true);
                }
            y = bottom - 12;
            Text($"ID bulto: {bulto.Id:D}", 8, Margin, y); y -= 12;
            Text($"Envío: {envio.Id:D}", 8, Margin, y); y -= 15;
            foreach (var (name, value) in fields)
            {
                Text(name.ToUpperInvariant(), 7, Margin, y); y -= size + 2;
                foreach (var line in Wrap(value, size)) { Text(line, size, Margin, y); y -= size + 2; }
                y -= 3;
            }

            decimal RequiredHeight(decimal candidate) => fields.Sum(f => (Wrap(f.Item2, candidate).Count + 1) * (candidate + 2) + 3);
            void Text(string value, decimal size, decimal x, decimal baseline) => page.AddText(value, size, P(x, baseline), font);
            List<string> Wrap(string value, decimal size)
            {
                var lines = new List<string>(); var line = "";
                foreach (var word in Regex.Split(value.Trim(), @"\s+"))
                {
                    if (Fits(line.Length == 0 ? word : line + " " + word)) { line = line.Length == 0 ? word : line + " " + word; continue; }
                    if (line.Length > 0) { lines.Add(line); line = ""; }
                    foreach (var character in word)
                    {
                        if (!Fits(line + character) && line.Length > 0) { lines.Add(line); line = ""; }
                        line += character;
                    }
                }
                if (line.Length > 0) lines.Add(line);
                return lines;
                bool Fits(string text) => page.MeasureText(text, size, P(0, 0), font)
                    .Select(l => l.GlyphRectangle.Right).DefaultIfEmpty(0).Max() <= (double)(Width - 2 * Margin);
            }
        }
        return builder.Build();
    }
    private static PdfPoint P(decimal x, decimal y) => new((double)x, (double)y);
    private static string Format(decimal value) => value.ToString("0.############################", Culture);
}
