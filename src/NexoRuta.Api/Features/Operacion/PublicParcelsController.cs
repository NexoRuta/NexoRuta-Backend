using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Seguimiento.Dtos;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Api.Features.Operacion;

[ApiController, AllowAnonymous, EnableRateLimiting("seguimiento-publico"), Route("api/public/seguimiento/{token}/bultos")]
public sealed class PublicParcelsController(NexoRutaDbContext db) : ControllerBase
{
    [HttpGet, ProducesResponseType<IReadOnlyList<BultoPublico>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Page(string token, int offset = 0, int limite = 200, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (offset is < 0 or > 10000 || limite is < 1 or > 200) return BadRequest();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var valid = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM nexoruta_app.public_tracking({token})").SingleAsync(ct);
        if (valid == 0) return NotFound();
        var parcels = await db.Database.SqlQuery<BultoPublico>($"SELECT * FROM nexoruta_app.public_parcel_details({token},{offset},{limite})").ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(parcels);
    }
}
