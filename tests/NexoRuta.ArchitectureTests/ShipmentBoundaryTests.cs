using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Envios.Controllers;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Domain.Envios;
using NexoRuta.Api.Features.Envios.Etiquetas;
using NexoRuta.Application.Envios.Dtos;
using UglyToad.PdfPig;

namespace NexoRuta.ArchitectureTests;

public sealed class ShipmentBoundaryTests
{
    [Theory]
    [InlineData(typeof(Envio))]
    [InlineData(typeof(CrearEnvioUseCase))]
    public void InnerLayers_DoNotReferenceHttpOrPersistence(Type type)
    {
        var references = type.Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(references, name => name!.StartsWith("Microsoft.AspNetCore"));
        Assert.DoesNotContain(references, name => name!.StartsWith("Microsoft.EntityFrameworkCore"));
        Assert.DoesNotContain("NexoRuta.Api", references);
        Assert.DoesNotContain("NexoRuta.Infrastructure", references);
    }

    [Fact]
    public void ShipmentEndpoints_DeclareApiOwnedResponses()
    {
        var endpoints = typeof(EnviosController).GetMethods()
            .Where(method => method.GetCustomAttributes(inherit: false)
                .OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any()).ToArray();
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Name == nameof(EnviosController.Etiquetas))
            {
                Assert.Equal("envios/{id:guid}/etiquetas", Assert.Single(endpoint.GetCustomAttributes(false).OfType<HttpGetAttribute>()).Template);
                Assert.Equal(new[] { "application/pdf" }, Assert.Single(endpoint.GetCustomAttributes(false).OfType<ProducesAttribute>()).ContentTypes);
                Assert.Equal(new[] { 200, 404, 422 }, endpoint.GetCustomAttributes(false).OfType<ProducesResponseTypeAttribute>().Select(a => a.StatusCode).Order());
                Assert.All(endpoint.GetCustomAttributes(false).OfType<ProducesResponseTypeAttribute>(), a => Assert.Equal(typeof(void), a.Type));
                Assert.Equal(typeof(Task<IActionResult>), endpoint.ReturnType);
                continue;
            }
            var responseTypes = endpoint.GetCustomAttributes(inherit: false)
                .OfType<ProducesResponseTypeAttribute>()
                .Select(attribute => attribute.Type)
                .Where(type => type != typeof(void)).ToArray();
            Assert.NotEmpty(responseTypes);
            foreach (var type in responseTypes)
            {
                var response = type.IsGenericType ? type.GenericTypeArguments[0] : type;
                Assert.Equal(typeof(EnviosController).Assembly, response.Assembly);
                Assert.Equal("NexoRuta.Api.Features.Envios.Contracts.Responses", response.Namespace);
            }
        }
    }

    [Fact]
    public void ShipmentLabels_BinaryContentContainsThePersistedParcelIdentity()
    {
        var parcel = Guid.NewGuid();
        var shipment = new NexoRuta.Application.Envios.Results.EnvioDetalle(Guid.NewGuid(),
            new OrigenEnvio(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sender@example.test", "Operator", "Commerce"),
            "Recipient", "Synthetic address", "Registrado", 1,
            [new BultoDetalle(parcel, "PARCEL-1", 1000, 10, 10, 10)], DateTimeOffset.UtcNow);
        var bytes = EtiquetasEnvioPdf.Generar(shipment);
        Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));
        using var pdf = PdfDocument.Open(bytes);
        var page = Assert.Single(pdf.GetPages());
        Assert.Contains(parcel.ToString("D"), page.Text);
        Assert.Contains("PARCEL-1", page.Text);
    }
}
