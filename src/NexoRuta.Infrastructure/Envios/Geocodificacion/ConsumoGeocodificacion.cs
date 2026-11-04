using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.Infrastructure.Envios.Geocodificacion;

public sealed class OpcionesGeocodificacion
{
    public int MaxSolicitudes24Horas { get; init; } = 500;
    public int SolicitudesPorSegundo { get; init; } = 3;
    public int SolicitudesPorMinutoUsuario { get; init; } = 10;
    public int TimeoutSegundos { get; init; } = 8;

    public static OpcionesGeocodificacion Leer(IConfiguration configuration)
    {
        var options = configuration.GetSection("Geocoding:Consumo").Get<OpcionesGeocodificacion>() ?? new();
        if (options.MaxSolicitudes24Horas is < 1 or > 2000 || options.SolicitudesPorSegundo is < 1 or > 3
            || options.SolicitudesPorMinutoUsuario is < 1 or > 60 || options.TimeoutSegundos is < 1 or > 30)
            throw new InvalidOperationException("Límites de geocodificación inválidos.");
        return options;
    }
}

public interface IConsumoGeocodificacion
{
    Task ReservarAsync(CancellationToken ct);
}

public sealed class ConsumoGeocodificacion(NexoRutaDbContext db, OpcionesGeocodificacion options) : IConsumoGeocodificacion
{
    public async Task ReservarAsync(CancellationToken ct)
    {
        // Commit the reservation independently: cancellation/failure must never refund a possibly billed request.
        await using var isolated = new NexoRutaDbContext((DbContextOptions<NexoRutaDbContext>)db.GetService<IDbContextOptions>());
        try
        {
            var state = await isolated.Database.SqlQuery<int>($"SELECT nexoruta_app.reserve_geocoding_request({options.MaxSolicitudes24Horas}, {options.SolicitudesPorSegundo}) AS \"Value\"").SingleAsync(ct);
            if (state != 0) throw new GeocodificacionLimiteException(state == 1);
        }
        catch (NpgsqlException) { throw new GeocodificacionNoDisponibleException(); }
    }
}
