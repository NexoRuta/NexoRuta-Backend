using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.IdentidadVisual.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.IdentidadVisual;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.Api.Features.IdentidadVisual.Controllers;

[ApiController]
[Authorize(Policy = CatalogoPermisos.ConfiguracionGestionar)]
[Route("api/configuracion/identidad-visual")]
public sealed class IdentidadVisualOperadorController(
    NexoRutaDbContext db,
    TimeProvider reloj) : ControllerBase
{
    private static readonly HashSet<string> CamposPermitidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "VersionEsperada", "NombreComercial", "CorreoPublico", "TelefonoPublico",
        "ColorPrimario", "ColorSecundario", "QuitarLogo", "Logo"
    };

    [HttpGet]
    [ProducesResponseType<IdentidadVisualAdministracionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Obtener(CancellationToken ct)
    {
        if (!TryOperator(out var context)) return Forbid();
        var current = await ObtenerVigenteAsync(context.OperadorId!.Value, ct);
        return Ok(Administracion(current, context.OperadorNombre ?? "Operador"));
    }

    [HttpGet("versiones")]
    [ProducesResponseType<IdentidadVisualHistorialResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Historial([FromQuery] int limite = 10,
        [FromQuery] int? antesDeVersion = null, CancellationToken ct = default)
    {
        if (!TryOperator(out var context)) return Forbid();
        if (limite is < 1 or > 20 || antesDeVersion is < 1)
            return BadRequest(new { message = "El límite debe estar entre 1 y 20 y la versión anterior debe ser positiva." });
        var query = db.VersionesIdentidadVisualOperador.AsNoTracking()
            .Where(item => item.OperadorId == context.OperadorId);
        if (antesDeVersion is { } cursor)
            query = query.Where(item => item.Version < cursor);
        var fetched = await query
            .OrderByDescending(item => item.Version)
            .Take(limite + 1)
            .Select(item => new IdentidadVisualVersionResponse(item.Version, item.AutorUsuarioId,
                item.CreadoEnUtc, item.NombreComercial, item.CorreoPublico, item.TelefonoPublico,
                item.ColorPrimario, item.ColorSecundario, item.LogoContenido, item.LogoTipoContenido))
            .ToListAsync(ct);
        var versions = fetched.Take(limite).ToArray();
        int? cursorAnterior = fetched.Count > limite ? versions[^1].Version : null;
        return Ok(new IdentidadVisualHistorialResponse(versions, cursorAnterior));
    }

    [HttpPut]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(1_250_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 1_250_000)]
    [ProducesResponseType<IdentidadVisualAdministracionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Actualizar([FromForm] ActualizarIdentidadVisualForm request, CancellationToken ct)
    {
        if (!TryOperator(out var context)) return Forbid();
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var form = await Request.ReadFormAsync(ct);
        if (form.Keys.Any(key => !CamposPermitidos.Contains(key)) || form.Files.Count > 1
            || form.Files.Any(file => !string.Equals(file.Name, "Logo", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { message = "La solicitud contiene campos no admitidos." });
        if (request.QuitarLogo && request.Logo is not null)
            return BadRequest(new { message = "Elegí cargar un logo nuevo o quitar el actual, no ambas acciones." });

        IdentidadVisualNormalizada values;
        try
        {
            values = ValidadorIdentidadVisual.Normalizar(request.NombreComercial, request.CorreoPublico,
                request.TelefonoPublico, request.ColorPrimario, request.ColorSecundario);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }

        LogoIdentidadVisual? uploadedLogo = null;
        if (request.Logo is { Length: > 0 } upload)
        {
            if (upload.Length > ValidadorIdentidadVisual.MaximoLogoBytes)
                return BadRequest(new { message = "El logo no puede superar 1 MiB." });
            await using var stream = new MemoryStream((int)upload.Length);
            await upload.CopyToAsync(stream, ct);
            try { uploadedLogo = ValidadorIdentidadVisual.ValidarLogo(stream.ToArray()); }
            catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
        }
        else if (request.Logo is not null)
            return BadRequest(new { message = "El archivo de logo está vacío." });

        var operatorId = context.OperadorId!.Value;
        var current = await ObtenerVigenteAsync(operatorId, ct);
        var currentVersion = current?.Version ?? 0;
        if (request.VersionEsperada != currentVersion)
            return Conflict(new { message = "La identidad visual cambió desde la última consulta. Recargá antes de guardar.", versionActual = currentVersion });

        var content = request.QuitarLogo ? null : uploadedLogo?.Contenido ?? current?.LogoContenido;
        var contentType = request.QuitarLogo ? null : uploadedLogo?.TipoContenido ?? current?.LogoTipoContenido;
        if (MismosDatos(current, values, content, contentType))
            return Ok(Administracion(current, context.OperadorNombre ?? "Operador"));

        var version = new VersionIdentidadVisualOperador(operatorId, checked(currentVersion + 1),
            values.NombreComercial, values.CorreoPublico, values.TelefonoPublico, values.ColorPrimario,
            values.ColorSecundario, content, contentType, context.UsuarioId, reloj.GetUtcNow());
        db.VersionesIdentidadVisualOperador.Add(version);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException exception) when (EsConflictoDeVersion(exception))
        {
            var actual = await ObtenerVigenteAsync(operatorId, ct);
            return Conflict(new { message = "Otra persona guardó cambios primero. Recargá la identidad visual antes de volver a intentar.", versionActual = actual?.Version ?? 0 });
        }

        return Ok(Administracion(version, context.OperadorNombre ?? "Operador"));
    }

    private bool TryOperator(out ContextoUsuario context)
    {
        if (HttpContext.Items[typeof(ContextoUsuario)] is ContextoUsuario user
            && user.TipoAcceso == TipoAccesoUsuario.Operador
            && user.OperadorId is { } id && id != Guid.Empty && user.UsuarioId != Guid.Empty)
        {
            context = user;
            return true;
        }
        context = null!;
        return false;
    }

    private Task<VersionIdentidadVisualOperador?> ObtenerVigenteAsync(Guid operatorId, CancellationToken ct)
        => db.VersionesIdentidadVisualOperador.AsNoTracking()
            .Where(item => item.OperadorId == operatorId)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(ct);

    private static bool MismosDatos(VersionIdentidadVisualOperador? current,
        IdentidadVisualNormalizada input, byte[]? logo, string? mediaType)
        => current is null
            ? input == new IdentidadVisualNormalizada(null, null, null, null, null) && logo is null && mediaType is null
            : current.NombreComercial == input.NombreComercial && current.CorreoPublico == input.CorreoPublico
                && current.TelefonoPublico == input.TelefonoPublico && current.ColorPrimario == input.ColorPrimario
                && current.ColorSecundario == input.ColorSecundario && current.LogoTipoContenido == mediaType
                && ((current.LogoContenido is null && logo is null)
                    || (current.LogoContenido is not null && logo is not null && current.LogoContenido.AsSpan().SequenceEqual(logo)));

    private static bool EsConflictoDeVersion(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static IdentidadVisualAdministracionResponse Administracion(VersionIdentidadVisualOperador? current, string fallback)
        => new(current?.Version ?? 0, current?.NombreComercial, current?.NombreComercial ?? fallback,
            current?.CorreoPublico, current?.TelefonoPublico, current?.ColorPrimario, current?.ColorSecundario,
            current?.LogoContenido, current?.LogoTipoContenido);

}

[ApiController]
[Authorize(Policy = nameof(TipoAccesoUsuario.Comercio))]
[Route("api/operadores/{operadorId:guid}/identidad-visual")]
public sealed class IdentidadVisualPublicaController(
    NexoRutaDbContext db,
    IOperadoresRepository operadores) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IdentidadVisualPublicaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(Guid operadorId, CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoUsuario)] is not ContextoUsuario context
            || context.TipoAcceso != TipoAccesoUsuario.Comercio || context.ComercioId is null)
            return Forbid();

        var operatorInfo = await operadores.ObtenerAsync(operadorId, ct);
        if (operatorInfo is null) return NotFound();
        await db.SelectOperatorAsync(operadorId, ct);
        var current = await db.VersionesIdentidadVisualOperador.AsNoTracking()
            .Where(item => item.OperadorId == operadorId)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(ct);
        return Ok(new IdentidadVisualPublicaResponse(current?.NombreComercial ?? operatorInfo.Nombre,
            current?.CorreoPublico, current?.TelefonoPublico, current?.ColorPrimario,
            current?.ColorSecundario, current?.LogoContenido, current?.LogoTipoContenido));
    }
}
