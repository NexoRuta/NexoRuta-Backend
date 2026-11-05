using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Reparto;

// Reuse the immutable operation journal: a partial visit fails only its undelivered parcels.
internal static class IntentosPorBulto
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task<Dictionary<Guid, ResumenIntentosEntrega>> Calcular(NexoRutaDbContext db,
        Guid op, Guid envio, IReadOnlyCollection<Bulto> bultos, int intervalo, CancellationToken ct)
    {
        var eventos = await db.OperacionesJornada.AsNoTracking().Where(o => o.OperadorId == op && o.EnvioId == envio
            && o.Resultado == "Aplicada" && (o.Tipo == "IntentoFallido" || o.Tipo == "EntregaBultos"))
            .Select(o => new { o.RutaId, o.Tipo, o.Payload }).ToArrayAsync(ct);
        var paradas = await db.ParadasRuta.AsNoTracking().Where(p => p.OperadorId == op && p.EnvioId == envio)
            .Select(p => new { p.RutaId, p.BultoIds }).ToDictionaryAsync(p => p.RutaId, ct);
        var historias = bultos.ToDictionary(b => b.Id, _ => new List<string>());
        foreach (var evento in eventos)
        {
            var operacion = JsonSerializer.Deserialize<OperacionRepartoDatos>(evento.Payload, Json)!;
            var datos = operacion.Datos.Deserialize<DatosOperacionReparto>(Json)!;
            var ids = evento.Tipo == "EntregaBultos"
                ? datos.ResultadosBultos?.Where(r => !r.Entregado).Select(r => r.BultoId).ToArray() ?? []
                : datos.BultoIds ?? (paradas.TryGetValue(evento.RutaId, out var p) && p.BultoIds.Length > 0 ? p.BultoIds : historias.Keys.ToArray());
            foreach (var id in ids) if (historias.TryGetValue(id, out var historia)) historia.Add(evento.Payload);
        }
        return historias.ToDictionary(h => h.Key, h => ResumenIntentosEntrega.Calcular(h.Value, intervalo));
    }
}
