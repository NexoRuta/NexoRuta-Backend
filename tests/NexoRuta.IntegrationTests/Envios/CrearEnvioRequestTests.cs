using System.ComponentModel.DataAnnotations;
using System.Globalization;
using NexoRuta.Api.Features.Envios.Contracts.Requests;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class CrearEnvioRequestTests
{
    [Theory]
    [InlineData("es-UY", "0.01", true)]
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
                OperadorId = Guid.CreateVersion7(), DestinatarioNombre = "Ana",
                Direccion = "Rivera 123", CodigoBulto = "B-001",
                PesoGramos = number, LargoCentimetros = number, AnchoCentimetros = number, AltoCentimetros = number
            };
            var errors = new List<ValidationResult>();
            Assert.Equal(expected, Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
            Assert.Equal(expected ? 0 : 4, errors.Count);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
