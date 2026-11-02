using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Envios.Repositories;

public sealed class EfDestinosEnvioRepository(NexoRutaDbContext db, TimeProvider reloj) : IDestinosEnvioRepository
{
    public Task<DestinoEnvioDatos?> ObtenerAsync(Guid comercioId, Guid envioId, CancellationToken ct)
        => (from e in db.Envios.AsNoTracking()
            join d in db.Direcciones.AsNoTracking() on new { e.OperadorId, DireccionId = e.DireccionId } equals new { d.OperadorId, DireccionId = d.Id }
            join destinatario in db.Destinatarios.AsNoTracking() on new { e.OperadorId, DestinatarioId = e.DestinatarioId } equals new { destinatario.OperadorId, DestinatarioId = destinatario.Id }
            where e.Id == envioId && e.ComercioId == comercioId && e.OperadorId == d.OperadorId
            select new DestinoEnvioDatos(e.Id, destinatario.Nombre, d.Descripcion, d.Version, e.Estado.ToString(), d.Latitud, d.Longitud, d.ConfirmadaEnUtc)).SingleOrDefaultAsync(ct);

    public async Task<bool> ConfirmarAsync(Guid comercioId, Guid usuarioId, Guid envioId, long version, double latitud, double longitud, CancellationToken ct, string? direccion = null)
    {
        var actual = await (from e in db.Envios.AsNoTracking() join d in db.Direcciones.AsNoTracking() on e.DireccionId equals d.Id
            where e.Id == envioId && e.ComercioId == comercioId && e.OperadorId == d.OperadorId
            select new { e.OperadorId, e.Estado, Direccion = d }).SingleOrDefaultAsync(ct);
        if (actual is null) return false;
        var puedeConfirmar = actual.Estado == EstadoEnvio.Admitido
            || actual.Estado == EstadoEnvio.EnDeposito && actual.Direccion.ConfirmadaEnUtc is null;
        if (actual.Direccion.Version != version || !puedeConfirmar)
            throw new ConflictoRepartoException("El destino o el envío cambiaron. Recargá antes de confirmar.");
        actual.Direccion.ConfirmarUbicacion(latitud, longitud, usuarioId, reloj.GetUtcNow(), version, direccion);
        var originalJson = await db.Database.SqlQuery<string>($"SELECT \"Resultado\"::text AS \"Value\" FROM public.\"TarifasEnvio\" WHERE \"EnvioId\"={envioId} ORDER BY \"CreadoEnUtc\",\"Id\" LIMIT 1").SingleOrDefaultAsync(ct);
        var original = originalJson is null ? null : JsonSerializer.Deserialize<ResultadoCalculoTarifa>(originalJson, TarifasAdmision.Json);
        if (original is null) throw new ConflictoRepartoException("El envío no tiene una tarifa aplicada verificable.");
        var ofertas = (await TarifasAdmision.OfertasAsync(db, actual.OperadorId, latitud, longitud, ct, original.VersionTarifarioId))
            .Where(o => o.Tarifa.Modalidad == original.Modalidad).ToArray();
        var elegida = ofertas.SingleOrDefault(o => o.Tarifa.ZonaId == original.ZonaId)
            ?? (ofertas.Length == 1 ? ofertas[0] : null);
        if (elegida is null) throw new ConflictoRepartoException("El operador no ofrece cobertura y tarifa inequívoca para el nuevo destino.");
        var bultos = await db.Bultos.Where(b => b.EnvioId == envioId && b.OperadorId == actual.OperadorId).ToArrayAsync(ct);
        var servicio = new ServicioEnvioDeclarado(elegida.Tarifa.ZonaId, latitud, longitud, original.Modalidad.ToString());
        var cargo = TarifasAdmision.Calcular(elegida.Tarifa, bultos);
        var textoDireccion = direccion?.Trim();
        try
        {
            var guardado = await db.Database.SqlQuery<bool>($"SELECT nexoruta_app.confirm_shipment_destination({envioId},{version},{latitud},{longitud},{textoDireccion}::text) AS \"Value\"").SingleAsync(ct);
            if (guardado) await TarifasAdmision.GuardarAsync(db, actual.OperadorId, envioId, servicio, cargo, ct);
            return guardado;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "NXR02")
        { throw new ConflictoRepartoException("El destino o el envío cambiaron. Recargá antes de confirmar."); }
    }
}
