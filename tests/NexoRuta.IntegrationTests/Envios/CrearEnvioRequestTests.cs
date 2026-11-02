using System.ComponentModel.DataAnnotations;
using System.Globalization;
using NexoRuta.Api.Features.Envios.Contracts.Requests;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class CrearEnvioRequestTests
{
    [Theory]
    [InlineData("es-UY", "0.01", true)]
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
                OperadorId = Guid.CreateVersion7(), DestinatarioNombre = "Ana", DestinatarioEmail = "destinatario@example.test",
                Direccion = "Rivera 123", Bultos = new List<NexoRuta.Api.Features.Envios.Contracts.Requests.BultoDeclaradoRequest> { new NexoRuta.Api.Features.Envios.Contracts.Requests.BultoDeclaradoRequest { Codigo = "B-001", PesoGramos = number, LargoCentimetros = number, AnchoCentimetros = number, AltoCentimetros = number } }
            };
            var errors = new List<ValidationResult>();
            Assert.Equal(expected, Validator.TryValidateObject(request.Bultos[0], new ValidationContext(request.Bultos[0]), errors, true));
            Assert.Equal(expected ? 0 : 4, errors.SelectMany(e => e.MemberNames).Distinct().Count());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
