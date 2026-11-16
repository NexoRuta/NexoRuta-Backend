using System.ComponentModel.DataAnnotations;
using System.Globalization;
using NexoRuta.Api.Features.Envios.Contracts.Requests;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class CrearEnvioRequestTests
{
    [Theory]
    [InlineData("es-UY", "0.01", true)]
    [InlineData("es-UY", "0", false)]
    [InlineData("es-UY", "-1", false)]
    [InlineData("es-UY", "1.230", true)]
    [InlineData("es-UY", "1.234", false)]
    [InlineData("en-US", "0", false)]
    [InlineData("en-US", "-1", false)]
    [InlineData("en-US", "1.230", true)]
    [InlineData("en-US", "1.234", false)]
    [InlineData("es-UY", "1.005", false)]
    [InlineData("es-UY", "1.000", true)]
    [InlineData("en-US", "1.005", false)]
    [InlineData("en-US", "1.00", true)]
    [InlineData("es-UY", "999999999", true)]
    [InlineData("es-UY", "0.009", false)]
    [InlineData("es-UY", "1000000000", false)]
    [InlineData("en-US", "0.01", true)]
    [InlineData("en-US", "999999999", true)]
    [InlineData("en-US", "0.009", false)]
    [InlineData("en-US", "1000000000", false)]
    public void DecimalLimits_AreIndependentOfProcessCulture(string culture, string value, bool expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var number = decimal.Parse(value, CultureInfo.InvariantCulture);
            var request = new CrearEnvioRequest
            {
                OperadorId = Guid.CreateVersion7(), Servicio = NexoRuta.IntegrationTests.Fixtures.ServiciosEnvioTestData.Declarar(Guid.CreateVersion7()), DestinatarioNombre = "Ana", DestinatarioEmail = "destinatario@example.test",
                Direccion = "Rivera 123", Bultos = new List<NexoRuta.Api.Features.Envios.Contracts.Requests.BultoDeclaradoRequest> { new NexoRuta.Api.Features.Envios.Contracts.Requests.BultoDeclaradoRequest { Codigo = "B-001", PesoGramos = number, LargoCentimetros = number, AnchoCentimetros = number, AltoCentimetros = number } }
            };
            var errors = new List<ValidationResult>();
            Assert.Equal(expected, Validator.TryValidateObject(request.Bultos[0], new ValidationContext(request.Bultos[0]), errors, true));
            Assert.Equal(expected ? 0 : 4, errors.SelectMany(e => e.MemberNames).Distinct().Count());
            foreach (var field in new[] { "PesoGramos", "LargoCentimetros", "AnchoCentimetros", "AltoCentimetros" })
            {
                var messages = errors.Where(e => e.MemberNames.Contains(field)).Select(e => e.ErrorMessage).ToArray();
                var outOfRange = number < 0.01m || number > 999999999m;
                var extraPrecision = decimal.Round(number, 2) != number;
                Assert.Equal((outOfRange ? 1 : 0) + (extraPrecision ? 1 : 0), messages.Length);
                if (outOfRange) Assert.Contains($"The field {field} must be between {0.01m} and {999999999m}.", messages);
                if (extraPrecision) Assert.Contains("Ingresá como máximo dos decimales efectivos.", messages);
            }
            Assert.Equal(number, request.Bultos[0].PesoGramos);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
    [Fact]
    public void VehiculosCompartenPrecisionSinHeredarElRangoDeBultos()
    {
        var request = new NexoRuta.Api.Features.Reparto.Contracts.VehiculoContract
        { Matricula = "ABC", PesoMaximoGramos = 9999999999999999m, VolumenMaximoCm3 = 9999999999999999999999m };
        Assert.True(Validator.TryValidateObject(request, new ValidationContext(request), [], true));
        var precision = new NexoRuta.Api.Validation.DosDecimalesAttribute();
        Assert.True(precision.IsValid(null)); Assert.False(precision.IsValid("1.23"));
        Assert.True(precision.IsValid(-1.230m)); Assert.True(precision.IsValid(0m));
        Assert.False(precision.IsValid(1.234m));
    }

}
