using NexoRuta.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Repositories;

namespace NexoRuta.IntegrationTests.Seguimiento;

internal static class SeguimientoTestData
{
    public static async Task<Envio> CreateEnvioAsync(NexoRutaDbContext db)
    {
        var operador = new Operador("Integration operator");
        var comercio = new Comercio("Integration commerce");
        var email = $"user-{Guid.NewGuid():N}@example.test";
        var usuario = new Usuario(email, AuthenticationTestData.Hash(email));
        var destinatario = new Destinatario(operador.Id, "Integration recipient");
        var direccion = new Direccion(operador.Id, "Integration address");
        var envio = new Envio(operador.Id, comercio.Id, usuario.Id, destinatario.Id, direccion.Id);
        db.AddRange(operador, comercio, usuario, destinatario, direccion, envio);
        await db.SaveChangesAsync();
        return envio;
    }

    public static async Task<Operador> CreateOtherOperadorAsync(NexoRutaDbContext db)
    {
        var operador = new Operador("Other integration operator");
        db.Operadores.Add(operador);
        await db.SaveChangesAsync();
        return operador;
    }

    public static Task<string> CreateActiveLinkAsync(NexoRutaDbContext db, Envio envio) =>
        new CrearEnlaceSeguimientoUseCase(
            new EfConsultaEstadoEnvio(db), new EfEnlacesSeguimientoRepository(db))
            .EjecutarAsync(envio.OperadorId, envio.Id);

    public static async Task<string> CreateExpiredLinkAsync(NexoRutaDbContext db, Envio envio)
    {
        var token = GeneradorTokenSeguimiento.Generar();
        var now = DateTimeOffset.UtcNow;
        db.EnlacesSeguimiento.Add(new EnlaceSeguimiento(
            envio.OperadorId, envio.Id, GeneradorTokenSeguimiento.CalcularHash(token),
            now.AddDays(-2), now.AddDays(-1)));
        await db.SaveChangesAsync();
        return token;
    }

    public static async Task<string> CreateRevokedLinkAsync(NexoRutaDbContext db, Envio envio)
    {
        var token = await CreateActiveLinkAsync(db, envio);
        var hash = GeneradorTokenSeguimiento.CalcularHash(token);
        var enlace = await db.EnlacesSeguimiento.SingleAsync(x => x.TokenHash == hash);
        enlace.Revocar(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return token;
    }
}
