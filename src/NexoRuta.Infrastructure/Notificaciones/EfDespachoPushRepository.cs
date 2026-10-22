using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Notificaciones;

public sealed class EfDespachoPushRepository(NexoRutaDbContext db)
{
    public async Task<LotePush> PendientesAsync(Guid mensajeId, Guid procesadorId, CancellationToken ct)
    {
        var datos = await db.Database.SqlQueryRaw<string>(
            "SELECT " + RlsSqlFunctionContract.PreparePush + "({0}, {1}) AS \"Value\"",
            mensajeId, procesadorId).SingleAsync(ct);
        return JsonSerializer.Deserialize<LotePush>(datos)
            ?? throw new InvalidDataException("El despacho push no devolvió un lote válido.");
    }

    public async Task RegistrarAsync(Guid mensajeId, Guid procesadorId, Guid instalacionId, string fidHash,
        string resultado, string? idFcm, CancellationToken ct)
    {
        if (resultado is not ("FcmAceptada" or "FidInvalido"))
            throw new ArgumentException("Resultado push no válido.");
        var guardado = await db.Database.SqlQueryRaw<bool>(
            "SELECT " + RlsSqlFunctionContract.RecordPush + "({0}, {1}, {2}, {3}, {4}, {5}) AS \"Value\"",
            mensajeId, procesadorId, instalacionId, fidHash, resultado, idFcm!).SingleAsync(ct);
        if (!guardado) throw new InvalidOperationException("No se pudo registrar el resultado push de la reserva.");
    }
}
