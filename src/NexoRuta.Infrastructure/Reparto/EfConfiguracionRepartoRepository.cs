using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class EfConfiguracionRepartoRepository(NexoRutaDbContext db) : IConfiguracionRepartoRepository
{
    public async Task<ConfiguracionRepartoDatos?> ObtenerAsync(Guid operadorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Contexto(operadorId, ct);
        var actual = await db.ConfiguracionesReparto.AsNoTracking().Where(x => x.OperadorId == operadorId)
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        await tx.CommitAsync(ct);
        return actual is null ? null : Datos(actual);
    }

    public async Task<ConfiguracionRepartoDatos> GuardarAsync(Guid operadorId, ConfiguracionRepartoDatos datos, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Contexto(operadorId, ct);
        await BloquearOperador(operadorId, ct);
        var actual = await db.ConfiguracionesReparto.Where(x => x.OperadorId == operadorId).MaxAsync(x => (long?)x.Version, ct) ?? 0;
        if (datos.Version != actual) throw new ConflictoRepartoException("La configuración cambió. Recargala antes de guardar.");
        var nueva = new ConfiguracionReparto(operadorId, checked(actual + 1), datos.ZonaHoraria, datos.MaximoParadas,
            datos.MaximoIntentos, datos.RequiereFirma, datos.RequiereFoto, datos.RequiereNombre,
            datos.RequiereDocumento, datos.RequiereFotoIntento, datos.BloquearDiscrepanciasCarga,
            datos.Motivos ?? throw new ArgumentException("Configurá los motivos de intento fallido."));
        db.ConfiguracionesReparto.Add(nueva);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Datos(nueva);
    }

    public async Task<IReadOnlyList<VehiculoDatos>> VehiculosAsync(Guid operadorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Contexto(operadorId, ct);
        var datos = await db.Vehiculos.AsNoTracking().Where(x => x.OperadorId == operadorId).OrderBy(x => x.Matricula)
            .Select(x => new VehiculoDatos(x.Id, x.Matricula, x.PesoMaximoGramos, x.VolumenMaximoCm3, x.Activo, x.Version)).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return datos;
    }

    public async Task<VehiculoDatos?> GuardarVehiculoAsync(Guid operadorId, VehiculoDatos datos, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Contexto(operadorId, ct);
        await BloquearOperador(operadorId, ct);
        Vehiculo vehiculo;
        if (datos.Id == Guid.Empty)
        {
            if (datos.Version != 0) throw new ConflictoRepartoException("La versión inicial del vehículo debe ser cero.");
            vehiculo = new Vehiculo(operadorId, datos.Matricula, datos.PesoMaximoGramos, datos.VolumenMaximoCm3);
            if (!datos.Activo) vehiculo.Actualizar(datos.Matricula, datos.PesoMaximoGramos, datos.VolumenMaximoCm3, false);
            db.Vehiculos.Add(vehiculo);
        }
        else
        {
            vehiculo = (await db.Vehiculos.SingleOrDefaultAsync(x => x.Id == datos.Id && x.OperadorId == operadorId, ct))!;
            if (vehiculo is null) return null;
            if (vehiculo.Version != datos.Version) throw new ConflictoRepartoException("El vehículo cambió. Recargalo antes de guardar.");
            vehiculo.Actualizar(datos.Matricula, datos.PesoMaximoGramos, datos.VolumenMaximoCm3, datos.Activo);
        }
        if (await db.Vehiculos.AnyAsync(x => x.OperadorId == operadorId && x.Matricula == vehiculo.Matricula && x.Id != vehiculo.Id, ct))
            throw new ConflictoRepartoException("Ya hay un vehículo con esa matrícula en el operador.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(vehiculo.Id, vehiculo.Matricula, vehiculo.PesoMaximoGramos, vehiculo.VolumenMaximoCm3, vehiculo.Activo, vehiculo.Version);
    }

    private static ConfiguracionRepartoDatos Datos(ConfiguracionReparto x) => new(x.Version, x.ZonaHoraria, x.MaximoParadas,
        x.MaximoIntentos, x.RequiereFirma, x.RequiereFoto, x.RequiereNombre, x.RequiereDocumento,
        x.RequiereFotoIntento, x.BloquearDiscrepanciasCarga, x.Motivos.ToArray());
    private Task Contexto(Guid operadorId, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT set_config('nexoruta.operador_id', {operadorId.ToString()}, true)", ct);
    private Task BloquearOperador(Guid operadorId, CancellationToken ct) => db.Operadores.FromSqlInterpolated(
        $"SELECT * FROM \"Operadores\" WHERE \"Id\" = {operadorId} FOR UPDATE").SingleAsync(ct);
}
