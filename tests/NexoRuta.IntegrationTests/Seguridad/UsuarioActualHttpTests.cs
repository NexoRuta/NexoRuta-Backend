using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class UsuarioActualHttpTests
{
    [Fact]
    public async Task UsuarioActual_ReutilizaLaConsultaDeAutenticacionEnLaMismaPeticion()
    {
        var cuenta = Cuenta();
        var cuentas = new Cuentas(cuenta);
        using var servicios = Servicios(cuentas);
        using var scope = servicios.CreateScope();
        var contexto = Contexto(scope.ServiceProvider, cuenta.AccesoId.ToString());
        await Autenticar(contexto);

        var usuarioActual = scope.ServiceProvider.GetRequiredService<IUsuarioActual>();
        Assert.Same(cuenta, await usuarioActual.ObtenerAsync());
        Assert.Same(cuenta, await usuarioActual.ObtenerAsync());
        Assert.Equal(1, cuentas.Consultas);
    }

    [Fact]
    public async Task UsuarioActual_VuelveAValidarCadaPeticionSinCompartirUsuarios()
    {
        var uno = Cuenta();
        var dos = Cuenta();
        var cuentas = new Cuentas(uno, dos);
        using var servicios = Servicios(cuentas);
        foreach (var cuenta in new[] { uno, dos })
        {
            using var scope = servicios.CreateScope();
            var contexto = Contexto(scope.ServiceProvider, cuenta.AccesoId.ToString());
            await Autenticar(contexto);
            Assert.Same(cuenta, await scope.ServiceProvider.GetRequiredService<IUsuarioActual>().ObtenerAsync());
        }
        Assert.Equal(2, cuentas.Consultas);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("invalido", 0)]
    [InlineData("00000000-0000-0000-0000-000000000001", 1)]
    public async Task UsuarioActual_RechazaAccesosAusentesInvalidosOInexistentes(string? acceso, int consultas)
    {
        var cuentas = new Cuentas(Cuenta());
        using var servicios = Servicios(cuentas);
        using var scope = servicios.CreateScope();
        var contexto = Contexto(scope.ServiceProvider, acceso);

        Assert.False((await contexto.AuthenticateAsync(AccesoSeleccionado.Esquema)).Succeeded);
        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() =>
            scope.ServiceProvider.GetRequiredService<IUsuarioActual>().ObtenerAsync());
        Assert.Equal(consultas, cuentas.Consultas);
    }

    [Fact]
    public async Task UsuarioActual_RechazaUnContextoQueNoCorrespondeAlPrincipal()
    {
        var cuenta = Cuenta();
        var cuentas = new Cuentas(cuenta);
        using var servicios = Servicios(cuentas);
        using var scope = servicios.CreateScope();
        var contexto = Contexto(scope.ServiceProvider, cuenta.AccesoId.ToString());
        await Autenticar(contexto);
        contexto.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(AccesoSeleccionado.ClaimAccesoId, Guid.CreateVersion7().ToString())
        ], AccesoSeleccionado.Esquema));

        await Assert.ThrowsAsync<AccesoActualNoDisponibleException>(() =>
            scope.ServiceProvider.GetRequiredService<IUsuarioActual>().ObtenerAsync());
        Assert.Equal(1, cuentas.Consultas);
    }

    [Fact]
    public async Task UsuarioActual_RespetaLaCancelacionDeLaPeticion()
    {
        var cuenta = Cuenta();
        var cuentas = new Cuentas(cuenta);
        using var servicios = Servicios(cuentas);
        using var scope = servicios.CreateScope();
        using var cancelacion = new CancellationTokenSource();
        var contexto = Contexto(scope.ServiceProvider, cuenta.AccesoId.ToString(), cancelacion.Token);
        await Autenticar(contexto);
        Assert.Equal(cancelacion.Token, cuentas.Cancelacion);

        cancelacion.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<IUsuarioActual>().ObtenerAsync(cancelacion.Token));
        Assert.Equal(1, cuentas.Consultas);
    }

    private static ContextoUsuario Cuenta() => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), null, Guid.CreateVersion7(),
        "dueno@comercio.local", null, "Comercio de prueba", true);

    private static ServiceProvider Servicios(Cuentas cuentas)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddHttpContextAccessor();
        servicios.AddSingleton<IAccesosUsuarioRepository>(cuentas);
        servicios.AddScoped<IUsuarioActual, UsuarioActualHttp>();
        servicios.AddAuthentication(AccesoSeleccionado.Esquema)
            .AddScheme<AuthenticationSchemeOptions, AccesoSeleccionadoHandler>(AccesoSeleccionado.Esquema, _ => { });
        return servicios.BuildServiceProvider();
    }

    private static HttpContext Contexto(IServiceProvider servicios, string? acceso, CancellationToken cancelacion = default)
    {
        var contexto = new DefaultHttpContext { RequestServices = servicios, RequestAborted = cancelacion };
        servicios.GetRequiredService<IHttpContextAccessor>().HttpContext = contexto;
        if (acceso is not null)
            contexto.Request.Headers[AccesoSeleccionado.Cabecera] = acceso;
        return contexto;
    }

    private static async Task Autenticar(HttpContext contexto)
    {
        var resultado = await contexto.AuthenticateAsync(AccesoSeleccionado.Esquema);
        Assert.True(resultado.Succeeded);
        contexto.User = resultado.Principal!;
    }

    private sealed class Cuentas(params ContextoUsuario[] cuentas) : IAccesosUsuarioRepository
    {
        public int Consultas { get; private set; }
        public CancellationToken Cancelacion { get; private set; }

        public Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Consultas++;
            Cancelacion = cancellationToken;
            return Task.FromResult(cuentas.SingleOrDefault(x => x.AccesoId == accesoId));
        }

        public Task<IReadOnlyList<ContextoUsuario>> ListarAsync(TipoAccesoUsuario tipo, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<OperadorDisponible>> ListarOperadoresAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperadorDisponible?> ObtenerOperadorAsync(Guid operadorId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
