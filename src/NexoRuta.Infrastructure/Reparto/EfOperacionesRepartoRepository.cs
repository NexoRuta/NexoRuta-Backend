using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfOperacionesRepartoRepository(NexoRutaDbContext db, TimeProvider reloj,
    IConfiguration configuracion) : IOperacionesRepartoRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Tipos = ["EntregaBultos", "RetirarBultos", "RetiroFallido", "InicioJornada", "CargarBulto", "ConfirmarCarga", "EntregaFisica",
        "EvidenciaEntrega", "IntentoFallido", "Rendicion", "Posicion", "FinJornada"];

    public async Task<ResultadoOperacionReparto?> AplicarAsync(Guid operadorId, Guid accesoId, Guid jornadaId,
        OperacionRepartoDatos operacion, CancellationToken ct)
    {
        if (operacion.Id == Guid.Empty || operacion.Secuencia < 1 || operacion.OcurridaEn == default
            || !Tipos.Contains(operacion.Tipo) || operacion.Datos.ValueKind != JsonValueKind.Object
            || System.Text.Encoding.UTF8.GetByteCount(operacion.Datos.GetRawText()) > 29_999_000)
            throw new ArgumentException("La operación está incompleta o excede el tamaño admitido.");
        var requiereEnvio = operacion.Tipo is "EntregaBultos" or "RetirarBultos" or "RetiroFallido" or "CargarBulto" or "EntregaFisica" or "EvidenciaEntrega" or "IntentoFallido";
        if (requiereEnvio ? operacion.EnvioId is null || operacion.EnvioId == Guid.Empty : operacion.EnvioId is not null)
            throw new ArgumentException("El envío no corresponde a esta operación.");
        DatosOperacionReparto datos;
        try { datos = operacion.Datos.Deserialize<DatosOperacionReparto>(Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new ArgumentException("Los datos de la operación no corresponden al contrato."); }
        ValidarDatos(datos);
        if (datos.ResultadosBultos is { } resultados && (resultados.Length < 1
            || resultados.Any(r => r.BultoId == Guid.Empty || r.Version < 0 || r.Motivo?.Length > 40)
            || resultados.Select(r => r.BultoId).Distinct().Count() != resultados.Length))
            throw new ArgumentException("Los resultados de bulto son inválidos o están repetidos.");
        var payload = JsonSerializer.Serialize(operacion, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        await using var tx = await db.BeginOrJoinAsync(ct);
        await ContextoYBloqueo(operadorId, ct);
        var hoja = await Hoja(operadorId, accesoId, jornadaId, ct);
        if (hoja is null) return null;
        var anterior = await db.OperacionesJornada.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operacion.Id, ct);
        if (anterior is not null)
        {
            if (anterior.AccesoId != accesoId || anterior.RutaId != hoja.Id || anterior.Hash != hash)
                throw new ConflictoRepartoException("El identificador ya fue usado con otro contenido o contexto.");
            var resultadoAnterior = JsonSerializer.Deserialize<ResultadoOperacionReparto>(anterior.Respuesta, Json)!;
            await tx.CommitAsync(ct); return resultadoAnterior;
        }
        if (operacion.Secuencia != hoja.UltimaSecuencia + 1)
            throw new ConflictoRepartoException("Sincronizá primero las operaciones anteriores de esta jornada.");
        await db.SetShipmentHistoryContextAsync("OperacionJornada", operacion.Id, operacion.Id,
            operacion.OcurridaEn, operacion.Tipo, ct);
        var resultado = "Aplicada"; string? detalle = null;
        try
        {
            if (datos.ResuelveOperacionId is { } resuelta)
            {
                var rechazo = await db.OperacionesJornada.AsNoTracking().SingleOrDefaultAsync(x => x.Id == resuelta, ct);
                Exigir(operacion.Tipo == "RetirarBultos" && rechazo is not null && rechazo.Resultado == "Conflicto"
                    && rechazo.Tipo == operacion.Tipo && rechazo.EnvioId == operacion.EnvioId && rechazo.RutaId == hoja.Id
                    && rechazo.AccesoId == accesoId, "La resolución debe referenciar un retiro rechazado de esta misma visita.");
            }
            detalle = await Ejecutar(hoja, operacion, datos, ct);
        }
        catch (ConflictoRepartoException ex)
        {
            // No guardar modificaciones parciales; conservar el hecho y su prueba como conflicto.
            db.ChangeTracker.Clear();
            hoja = (await Hoja(operadorId, accesoId, jornadaId, ct))!;
            resultado = "Conflicto"; detalle = ex.Message;
        }
        hoja.ConfirmarSecuencia(operacion.Secuencia);
        long? versionEnvio = operacion.EnvioId is { } envioId
            ? (await db.Envios.SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == envioId, ct))?.Version : null;
        var cambios = resultado == "Aplicada" ? db.ChangeTracker.Entries<Envio>()
            .Where(x => x.State == EntityState.Modified).ToDictionary(x => x.Entity.Id, x => x.Entity.Version) : new Dictionary<Guid, long>();
        var estados = resultado == "Aplicada" ? db.ChangeTracker.Entries<Envio>().Where(x => x.State == EntityState.Modified)
            .ToDictionary(x => x.Entity.Id, x => x.Entity.Estado.ToString()) : new Dictionary<Guid, string>();
        var respuesta = new ResultadoOperacionReparto(operacion.Id, operacion.Secuencia, resultado, detalle, versionEnvio, hoja.Version, cambios, estados) { VersionesBulto = resultado == "Aplicada" ? db.ChangeTracker.Entries<Bulto>().Where(x => x.State == EntityState.Modified).ToDictionary(x => x.Entity.Id, x => x.Entity.Version) : new Dictionary<Guid, long>() };
        var recibidaEn = reloj.GetUtcNow();
        var incidencias = resultado == "Aplicada" && operacion.Tipo == "FinJornada"
            ? (await db.Bultos.AsNoTracking().Where(b => b.OperadorId == operadorId && b.RutaCustodiaId == hoja.Id
                && b.AccesoCustodioId == accesoId && b.Custodia == EstadoCustodiaBulto.Repartidor).OrderBy(b => b.Id).ToArrayAsync(ct))
                .Select(b => new IncidenciaCierreDatos(operacion.Id, b.EnvioId, b.Id, b.Codigo, b.Version,
                    operacion.OcurridaEn.ToUniversalTime(), recibidaEn)).ToArray()
            : [];
        db.OperacionesJornada.Add(new()
        {
            Id = operacion.Id, OperadorId = operadorId, AccesoId = accesoId, RutaId = hoja.Id,
            EnvioId = hoja.Paradas.Any(x => x.EnvioId == operacion.EnvioId) ? operacion.EnvioId : null,
            Secuencia = operacion.Secuencia, Tipo = operacion.Tipo, Hash = hash, Payload = payload,
            Respuesta = JsonSerializer.Serialize(respuesta, Json), Resultado = resultado, RecibidaEnUtc = recibidaEn,
            IncidenciasCierre = JsonSerializer.Serialize(incidencias, Json)
        });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return respuesta;
    }

    private async Task<HojaRuta?> Hoja(Guid op, Guid acceso, Guid jornada, CancellationToken ct) =>
        await db.HojasRuta.Include(x => x.Paradas).SingleOrDefaultAsync(x => x.OperadorId == op && x.RepartidorAccesoId == acceso && x.JornadaId == jornada, ct);
    private async Task ContextoYBloqueo(Guid op, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('nexoruta.operador_id', {op.ToString()}, true)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Operadores\" WHERE \"Id\" = {op} FOR UPDATE", ct);
    }
    private static void Exigir(bool condicion, string mensaje) { if (!condicion) throw new ConflictoRepartoException(mensaje); }
    private static void ValidarDatos(DatosOperacionReparto x)
    {
        if (x.Nombre?.Length > 160 || x.Documento?.Length > 40 || x.Motivo?.Length > 500
            || x.BultoIds is { } ids && (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            || x.Latitud is { } lat && (!double.IsFinite(lat) || lat is < -90 or > 90)
            || x.Longitud is { } lon && (!double.IsFinite(lon) || lon is < -180 or > 180)
            || x.PrecisionMetros is { } precision && (!double.IsFinite(precision) || precision <= 0 || precision > 100000)
            || x.PosicionEnUtc == default(DateTimeOffset))
            throw new ArgumentException("Los datos de la operación contienen valores inválidos.");
    }
}
