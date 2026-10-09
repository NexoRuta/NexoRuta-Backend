using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Controllers;
using NexoRuta.Application.Envios;
using NexoRuta.Domain.Envios;

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
        var responseTypes = typeof(EnviosController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(inherit: false))
            .OfType<ProducesResponseTypeAttribute>()
            .Select(attribute => attribute.Type)
            .Where(type => type != typeof(void)).ToArray();
        Assert.Equal(2, responseTypes.Length);
        foreach (var type in responseTypes)
        {
            var response = type.IsGenericType ? type.GenericTypeArguments[0] : type;
            Assert.Equal(typeof(EnviosController).Assembly, response.Assembly);
            Assert.Equal("NexoRuta.Api.Contracts.Envios", response.Namespace);
        }
    }
}
