using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace NexoRuta.Application.Envios.Importacion;

public enum CsvEnviosFileErrorCode
{
    InvalidEncoding,
    InvalidCsv,
    InvalidHeader,
    FileTooLarge,
    TooManyRecords
}

public sealed class CsvEnviosFileException(CsvEnviosFileErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public CsvEnviosFileErrorCode Code { get; } = code;
}

public sealed record CsvEnviosRecord(int Record, IReadOnlyList<string> Fields);

public static class CsvEnviosParser
{
    public const int MaximumFileBytes = 5 * 1024 * 1024;
    public const int MaximumDataRecords = 500;

    private static readonly string[] RequiredHeader =
    [
        "referenciaExterna", "nombreDestinatario", "emailDestinatario", "direccion", "codigoBulto",
        "pesoGramos", "largoCentimetros", "anchoCentimetros", "altoCentimetros"
    ];

    public static IReadOnlyList<CsvEnviosRecord> Parse(ReadOnlyMemory<byte> content)
    {
        if (content.Length > MaximumFileBytes)
            throw new CsvEnviosFileException(CsvEnviosFileErrorCode.FileTooLarge, "El archivo supera el límite de 5 MiB.");

        string csv;
        try
        {
            var bytes = content.Span;
            if (bytes.StartsWith(Encoding.UTF8.Preamble))
                bytes = bytes[Encoding.UTF8.Preamble.Length..];
            // Strip ignored trailing blank records before TextFieldParser scans them one by one.
            csv = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes)
                .TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException error)
        {
            throw new CsvEnviosFileException(CsvEnviosFileErrorCode.InvalidEncoding, "El archivo debe estar codificado en UTF-8 válido.", error);
        }

        using var parser = new TextFieldParser(new StringReader(csv))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

        string[]? header;
        try
        {
            header = parser.ReadFields();
        }
        catch (MalformedLineException error)
        {
            throw new CsvEnviosFileException(CsvEnviosFileErrorCode.InvalidCsv, "El encabezado CSV está malformado.", error);
        }
        if (header is null || !header.SequenceEqual(RequiredHeader, StringComparer.Ordinal))
            throw new CsvEnviosFileException(CsvEnviosFileErrorCode.InvalidHeader, "El encabezado CSV no coincide con la plantilla requerida.");

        var rows = new List<CsvEnviosRecord>();
        var record = 0;
        try
        {
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                if (fields is null)
                    break;
                record++;
                if (fields.Length == 1 && fields[0].Length == 0)
                    continue;
                if (rows.Count == MaximumDataRecords)
                    throw new CsvEnviosFileException(CsvEnviosFileErrorCode.TooManyRecords, "El archivo supera el límite de 500 registros de datos.");
                rows.Add(new CsvEnviosRecord(record, fields));
            }
        }
        catch (MalformedLineException error)
        {
            throw new CsvEnviosFileException(CsvEnviosFileErrorCode.InvalidCsv, "El contenido del archivo no es un CSV válido.", error);
        }

        return rows;
    }
}
