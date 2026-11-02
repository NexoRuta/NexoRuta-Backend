using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Infrastructure.Envios.Geocodificacion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class ConsumoGeocodificacionPostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task CuotaSeComparteEntreInstanciasConcurrentesYNoSeReiniciaAlRecrearServicio()
    {
        var data = await postgres.CreateDatabaseAsync();
        var options = new OpcionesGeocodificacion { MaxSolicitudes24Horas = 2, SolicitudesPorSegundo = 3 };
        async Task<bool> Reserve()
        {
            await using var db = data.CreateDbContext();
            try { await new ConsumoGeocodificacion(db, options).ReservarAsync(default); return true; }
            catch (GeocodificacionLimiteException e) { Assert.True(e.CuotaAgotada); return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Reserve()));
        Assert.Equal(2, results.Count(x => x));
        Assert.False(await Reserve());
        await using var check = data.CreateDbContext();
        Assert.Equal(2, await check.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM public.\"UsoGeocodificacion\"").SingleAsync());
    }

    [Fact]
    public async Task LimitePorSegundoYReservasFallidasNoRestituyenCupo()
    {
        var data = await postgres.CreateDatabaseAsync();
        await using var db = data.CreateDbContext();
        var budget = new ConsumoGeocodificacion(db, new() { MaxSolicitudes24Horas = 2, SolicitudesPorSegundo = 1 });
        await budget.ReservarAsync(default);
        var limited = await Assert.ThrowsAsync<GeocodificacionLimiteException>(() => budget.ReservarAsync(default));
        Assert.False(limited.CuotaAgotada);
        // Move only test-owned accounting timestamps, never operational destinations.
        await db.Database.ExecuteSqlRawAsync("UPDATE public.\"UsoGeocodificacion\" SET \"ReservadaEnUtc\"=clock_timestamp()-interval '2 seconds'");
        await budget.ReservarAsync(default);
        var exhausted = await Assert.ThrowsAsync<GeocodificacionLimiteException>(() => budget.ReservarAsync(default));
        Assert.True(exhausted.CuotaAgotada);
        await db.Database.ExecuteSqlRawAsync("UPDATE public.\"UsoGeocodificacion\" SET \"ReservadaEnUtc\"=clock_timestamp()-interval '25 hours'");
        await budget.ReservarAsync(default);
    }
}
