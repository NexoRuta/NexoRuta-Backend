using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Api.Features.Messaging;

public sealed record InternalRetryResult(Guid SolicitudId, string Resultado);
public sealed record RetryInternalMessage(Guid SolicitudId, int Intentos, string Motivo);
public sealed class FailedInternalMessage
{
    public Guid Id { get; set; }
    public string Tipo { get; set; } = "";
    public string Destino { get; set; } = "";
    public string Estado { get; set; } = "";
    public int Intentos { get; set; }
    public string Diagnostico { get; set; } = "";
    public DateTimeOffset ProximoIntentoEnUtc { get; set; }
}

[ApiController, Route("api/admin/mensajes-internos")]
[Authorize(Policy = "configuracion.gestionar"), Authorize(Policy = "envios.consultar")]
public sealed class InternalMessagesController(NexoRutaDbContext db, IUsuarioActual current) : ControllerBase
{
    [HttpGet, ProducesResponseType<FailedInternalMessage[]>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(int pagina = 1, int tamano = 25, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        if ((await current.ObtenerAsync(ct)).OperadorId is null) return Forbid();
        if (pagina is < 1 or > 100000 || tamano is < 1 or > 100) return BadRequest();
        return Ok(await db.Database.SqlQuery<FailedInternalMessage>(
            $"SELECT * FROM nexoruta_app.failed_outbox_messages({pagina},{tamano})").ToArrayAsync(ct));
    }

    [HttpPost("{id:guid}/reintentos"), ProducesResponseType<InternalRetryResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Retry(Guid id, RetryInternalMessage request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if ((await current.ObtenerAsync(ct)).OperadorId is null) return Forbid();
        if (id == Guid.Empty || request.SolicitudId == Guid.Empty || request.Intentos < 0
            || string.IsNullOrWhiteSpace(request.Motivo) || request.Motivo.Trim().Length > 500) return BadRequest();
        try
        {
            var result = await db.Database.SqlQuery<string>(
                $"""SELECT nexoruta_app.request_outbox_retry({request.SolicitudId},{id},{request.Intentos},{request.Motivo}) AS "Value" """).SingleAsync(ct);
            // Business outcomes use 200 so the request transaction commits the audit of refusals too.
            return Ok(new InternalRetryResult(request.SolicitudId, result));
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == "22023")
        { return BadRequest(new { message = "La identidad de solicitud ya se usó con otros datos." }); }
    }
}
