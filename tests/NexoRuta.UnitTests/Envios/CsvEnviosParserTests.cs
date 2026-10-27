using System.Text;
using NexoRuta.Application.Envios.Importacion;

namespace NexoRuta.UnitTests.Envios;

public sealed class CsvEnviosParserTests
{
    private const string Header = "referenciaExterna,nombreDestinatario,emailDestinatario,direccion,codigoBulto,pesoGramos,largoCentimetros,anchoCentimetros,altoCentimetros";
    private const string Row = "REF-1,María,maria@example.test,Dirección,BX-1,1200.50,30,20,10";

    [Fact]
    public void Parse_AcceptsQuotedCommasEscapesAndEmbeddedNewlines()
    {
        var bytes = Encoding.UTF8.GetBytes(Header + "\r\nREF-2,\"Pérez, María\",maria@example.test,\"Calle 1\nPiso 2\",\"BX-\"\"2\"\"\",1200.50,30,20,10");

        var rows = CsvEnviosParser.Parse(bytes);

        Assert.Single(rows);
        Assert.Equal("Pérez, María", rows[0].Fields[1]);
        Assert.Equal("Calle 1\nPiso 2", rows[0].Fields[3]);
        Assert.Equal("BX-\"2\"", rows[0].Fields[4]);
    }

    [Fact]
    public void Parse_AcceptsOptionalUtf8BomAndIgnoresEmptyRecords()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Header + "\n\n" + Row + "\n")).ToArray();

        var rows = CsvEnviosParser.Parse(bytes);

        Assert.Single(rows);
        Assert.Equal(1, rows[0].Record);
    }

    [Fact]
    public void Parse_AcceptsValidRecordAtMaximumFileSizeWithTrailingEmptyRecords()
    {
        var validCsv = Encoding.UTF8.GetBytes(Header + "\nREF-LIMIT,Ada,ada@example.test,Street 1,BOX,500,20,15,10");
        var bytes = new byte[CsvEnviosParser.MaximumFileBytes];
        Array.Copy(validCsv, bytes, validCsv.Length);
        Array.Fill(bytes, (byte)'\n', validCsv.Length, bytes.Length - validCsv.Length);

        var rows = CsvEnviosParser.Parse(bytes);

        Assert.Single(rows);
        Assert.Equal("REF-LIMIT", rows[0].Fields[0]);
    }

    [Fact]
    public void Parse_RejectsMalformedUtf8AndQuotedCsv()
    {
        var invalidUtf8 = Encoding.UTF8.GetBytes(Header + "\n").Concat(new byte[] { 0xC3, 0x28 }).ToArray();
        Assert.Equal(CsvEnviosFileErrorCode.InvalidEncoding,
            Assert.Throws<CsvEnviosFileException>(() => CsvEnviosParser.Parse(invalidUtf8)).Code);

        var malformed = Encoding.UTF8.GetBytes(Header + "\n\"REF-1,unterminated");
        Assert.Equal(CsvEnviosFileErrorCode.InvalidCsv,
            Assert.Throws<CsvEnviosFileException>(() => CsvEnviosParser.Parse(malformed)).Code);
    }

    [Fact]
    public void Parse_RequiresExactHeaderAndEnforcesExactByteAndRecordLimits()
    {
        var invalidHeader = Encoding.UTF8.GetBytes(Header.Replace("emailDestinatario", "email", StringComparison.Ordinal) + "\n" + Row);
        Assert.Equal(CsvEnviosFileErrorCode.InvalidHeader,
            Assert.Throws<CsvEnviosFileException>(() => CsvEnviosParser.Parse(invalidHeader)).Code);

        var exactPrefix = Header + "\nREF-1,";
        var exactSuffix = ",maria@example.test,Dirección,BX-1,1200.50,30,20,10";
        var exact = Encoding.UTF8.GetBytes(exactPrefix
            + new string('a', CsvEnviosParser.MaximumFileBytes - Encoding.UTF8.GetByteCount(exactPrefix + exactSuffix))
            + exactSuffix);
        Assert.Equal(CsvEnviosParser.MaximumFileBytes, exact.Length);
        Assert.Single(CsvEnviosParser.Parse(exact));
        Assert.Equal(CsvEnviosFileErrorCode.FileTooLarge,
            Assert.Throws<CsvEnviosFileException>(() => CsvEnviosParser.Parse(exact.Concat(new byte[] { (byte)'\n' }).ToArray())).Code);

        var header = Encoding.UTF8.GetBytes(Header + "\n");
        var maximumRows = string.Join("\n", Enumerable.Range(1, CsvEnviosParser.MaximumDataRecords)
            .Select(i => $"REF-{i},N,n@example.test,D,B,1,1,1,1"));
        Assert.Equal(CsvEnviosParser.MaximumDataRecords,
            CsvEnviosParser.Parse(header.Concat(Encoding.UTF8.GetBytes(maximumRows)).ToArray()).Count);
        var rows = string.Join("\n", Enumerable.Range(1, CsvEnviosParser.MaximumDataRecords + 1)
            .Select(i => $"REF-{i},N,n@example.test,D,B,1,1,1,1"));
        Assert.Equal(CsvEnviosFileErrorCode.TooManyRecords,
            Assert.Throws<CsvEnviosFileException>(() => CsvEnviosParser.Parse(header.Concat(Encoding.UTF8.GetBytes(rows)).ToArray())).Code);
    }
}
