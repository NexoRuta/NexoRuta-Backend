using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Gestion;
using NexoRuta.Application.Gestion;
using NexoRuta.Domain.Gestion;
namespace NexoRuta.ArchitectureTests;
public sealed class ManagementBoundaryTests
{
    [Theory]
    [InlineData(typeof(ReglasGestion))]
    [InlineData(typeof(GestionUseCases))]
    public void DomainAndApplicationRemainIndependent(Type type)
    {
        var references=type.Assembly.GetReferencedAssemblies().Select(x=>x.Name).ToArray();
        Assert.DoesNotContain(references,x=>x!.StartsWith("Microsoft.AspNetCore") || x.StartsWith("Microsoft.EntityFrameworkCore"));
        Assert.DoesNotContain("NexoRuta.Api",references);Assert.DoesNotContain("NexoRuta.Infrastructure",references);
    }
    [Fact]
    public void HttpContractsBelongToApi()
    {
        var methods=typeof(GestionController).GetMethods().Where(x=>x.GetCustomAttributes(false).OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any());
        foreach(var method in methods)
        {
            var types=method.GetCustomAttributes(false).OfType<ProducesResponseTypeAttribute>().Where(x=>x.Type!=typeof(void)).Select(x=>x.Type).ToArray();
            Assert.NotEmpty(types);Assert.All(types,x=>Assert.Equal(typeof(GestionController).Assembly,x.Assembly));
        }
    }
}
