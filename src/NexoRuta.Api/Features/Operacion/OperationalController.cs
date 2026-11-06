using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Infrastructure.Operacion;

namespace NexoRuta.Api.Features.Operacion;

[ApiController, Route("api/operacion"), Authorize(Policy = "envios.consultar")]
public sealed class OperationalController(IUsuarioActual usuario, OperationalReadStore read, IServiceProvider services) : ControllerBase
{
    [HttpGet("fotografia")]
    public async Task<IActionResult> Snapshot(CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (actor.ComercioId is not null || actor.OperadorId is not { } op) return Forbid();
        return Ok(await read.SnapshotAsync(op, ct));
    }

    [HttpGet("analitica/volumen")]
    public async Task<IActionResult> Volumes(Guid operadorId, DateTimeOffset desde, DateTimeOffset hasta, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (operadorId == Guid.Empty || actor.ComercioId is null && actor.OperadorId != operadorId) return Forbid();
        var mongo = services.GetService<OperationalMongoStore>();
        if (mongo is null) return Problem("La analítica documental está indisponible.", statusCode: 503);
        if (actor.ComercioId is not null)
        {
            var db = services.GetRequiredService<NexoRuta.Infrastructure.Persistence.NexoRutaDbContext>();
            await NexoRuta.Infrastructure.Persistence.RlsTransaction.SelectOperatorAsync(db, operadorId, ct);
        }
        try
        {
            var before = await read.ReadHealthAsync("history", operadorId, ct);
            var volumes = await mongo.VolumesAsync(operadorId, actor.ComercioId, desde, hasta, ct);
            var after = await read.ReadHealthAsync("history", operadorId, ct);
            var status = before.Estado == "Actualizada" && after.Estado == "Actualizada" && before.Capturados == after.Capturados
                && before.Documentados == after.Documentados ? "Actualizada" : after.Estado == "Indisponible" ? "Indisponible" : "Atrasada";
            return Ok(new { estadoProyeccion = status, verificadaEnUtc = after.VerificadaEnUtc, retencionDias = OperationalMongoStore.RetentionDays, desde, hasta, volumenPorTipo = volumes });
        }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: 400); }
        catch (Exception error) when (error is MongoException or TimeoutException)
        { return Problem("La analítica documental está indisponible. Reintentá más tarde.", statusCode: 503); }
    }

    [HttpGet("historial")]
    public async Task<IActionResult> History(Guid operadorId, DateTimeOffset desde, DateTimeOffset hasta,
        Guid? envioId = null, string? tipo = null, int offset = 0, int limite = 50, CancellationToken ct = default)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (operadorId == Guid.Empty || actor.ComercioId is null && actor.OperadorId != operadorId) return Forbid();
        if (actor.ComercioId is not null)
        {
            var db = services.GetRequiredService<NexoRuta.Infrastructure.Persistence.NexoRutaDbContext>();
            await NexoRuta.Infrastructure.Persistence.RlsTransaction.SelectOperatorAsync(db, operadorId, ct);
        }
        var mongo = services.GetService<OperationalMongoStore>();
        if (mongo is null) return Problem("El historial documental está indisponible.", statusCode: 503);
        try
        {
            var before = await read.ReadHealthAsync("history", operadorId, ct);
            var items = await mongo.QueryAsync(operadorId, actor.ComercioId, envioId, tipo, desde, hasta, offset, limite, ct);
            var after = await read.ReadHealthAsync("history", operadorId, ct);
            var status = before.Estado == "Actualizada" && after.Estado == "Actualizada" && before.Capturados == after.Capturados
                && before.Documentados == after.Documentados ? "Actualizada" : after.Estado == "Indisponible" ? "Indisponible" : "Atrasada";
            return Ok(new
            {
                estadoProyeccion = status,
                verificadaEnUtc = after.VerificadaEnUtc,
                retencionDias = OperationalMongoStore.RetentionDays,
                offset,
                limite,
                datos = items.Select(x => JsonSerializer.Deserialize<JsonElement>(x)).ToArray()
            });
        }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: 400); }
        catch (Exception error) when (error is MongoException or TimeoutException)
        { return Problem("El historial documental está indisponible. Reintentá más tarde.", statusCode: 503); }
    }
}
