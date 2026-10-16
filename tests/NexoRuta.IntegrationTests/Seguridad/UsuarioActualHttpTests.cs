using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class UsuarioActualHttpTests
{
    [Fact]
    public async Task UsuarioActual_ReutilizaElContextoAutorizadoDeLaPeticion()
    {
        var account = Account();
        var current = Current(account, account.AccesoId);

        Assert.Same(account, await current.ObtenerAsync());
        Assert.Same(account, await current.ObtenerAsync());
    }

    [Fact]
    public async Task UsuarioActual_NoComparteContextoEntrePeticiones()
    {
        var first = Account();
        var second = Account();

        Assert.Same(first, await Current(first, first.AccesoId).ObtenerAsync());
        Assert.Same(second, await Current(second, second.AccesoId).ObtenerAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UsuarioActual_RechazaContextoAusenteInvalidoOMismatch(bool addContext, bool matchingClaim)
    {
        var account = Account();
        var claimAccessId = matchingClaim ? account.AccesoId : Guid.CreateVersion7();
        var current = Current(addContext ? account : null, claimAccessId);

        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() => current.ObtenerAsync());
    }

    [Fact]
    public async Task UsuarioActual_RespetaLaCancelacion()
    {
        using var cancellation = new CancellationTokenSource();
        var current = Current(Account(), Guid.CreateVersion7());
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => current.ObtenerAsync(cancellation.Token));
    }

    private static ContextoUsuario Account() => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), null, Guid.CreateVersion7(),
        "owner@commerce.local", null, "Test commerce", Guid.CreateVersion7(), "dynamic-role");

    private static IUsuarioActual Current(ContextoUsuario? account, Guid? claimAccessId)
    {
        var context = new DefaultHttpContext();
        if (account is not null) context.Items[typeof(ContextoUsuario)] = account;
        if (claimAccessId is { } accessId)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(AccesoSeleccionado.ClaimAccesoId, accessId.ToString())
            ], "Bearer"));
        return new UsuarioActualHttp(new HttpContextAccessor { HttpContext = context });
    }
}
