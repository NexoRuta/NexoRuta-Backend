using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;
using NexoRuta.Api.Features.ZonasCobertura.Contracts;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.ZonasCobertura;

namespace NexoRuta.Api.Features.ZonasCobertura.Controllers;

[ApiController]
[Authorize(Policy = "organizacion")]
[Authorize(Policy = "configuracion.gestionar")]
[Route("api/configuracion/zonas")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class ZonasCoberturaController(
    GestionarZonasCoberturaUseCase zonas,
    ConsultarCoberturaUseCase cobertura,
    ILogger<ZonasCoberturaController> logger) : ControllerBase
{
    private const int MaxRequestBytes = 1_048_576;

    [HttpGet]
    [ProducesResponseType<PaginaZonasCoberturaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> Listar([FromQuery, System.ComponentModel.DataAnnotations.RegularExpression("^(Activa|Inactiva)$")] string? estado, [FromQuery] int pagina = 1,
        [FromQuery] int tamanoPagina = 20, CancellationToken ct = default) => Ejecutar(async () =>
    {
        if (!TryEstadoZona(estado, out var status)) return BadRequest(new { message = "El estado debe ser Activa o Inactiva." });
        var (items, total) = await zonas.ListarAsync(status ?? EstadoZonaCobertura.Activa, pagina, tamanoPagina, ct);
        var response = new PaginaZonasCoberturaResponse(items.Select(x => new ZonaCoberturaResumenResponse(
            x.Id, x.Nombre, x.Estado.ToString(), x.FranjasActivas, x.Version, x.RevisionActual, x.ActualizadaUtc)).ToArray(),
            pagina, tamanoPagina, total);
        return Ok(response);
    });

    [HttpGet("{zonaId:guid}")]
    [ProducesResponseType<ZonaCoberturaDetalleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Obtener(Guid zonaId, CancellationToken ct) => Ejecutar(async () =>
    {
        var zone = await zonas.ObtenerAsync(zonaId, ct);
        return zone is null ? NotFound(Error("No se encontró la zona.")) : Ok(ZonaCoberturaResponseMapper.Desde(zone));
    });

    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<ZonaCoberturaDetalleResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Crear([FromBody] CrearZonaCoberturaRequest request, CancellationToken ct) => Ejecutar(async () =>
    {
        var hasGeometry = request.Geometria is not null;
        var hasDepartment = !string.IsNullOrWhiteSpace(request.CodigoDepartamento);
        if (hasGeometry == hasDepartment)
            return RechazarBadRequest("Debe indicarse exactamente una geometría o un departamento.",
                "Indicá exactamente una geometría GeoJSON o un código de departamento.");

        var id = hasGeometry
            ? await zonas.CrearAsync(request.Nombre, LeerGeometria(request.Geometria!), ct)
            : await zonas.CrearDesdeDepartamentoAsync(request.Nombre, request.CodigoDepartamento!, ct);
        var created = await zonas.ObtenerAsync(id, ct);
        return created is null
            ? Problem(statusCode: StatusCodes.Status500InternalServerError)
            : CreatedAtAction(nameof(Obtener), new { zonaId = id }, ZonaCoberturaResponseMapper.Desde(created));
    });

    [HttpPut("{zonaId:guid}")]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<ZonaCoberturaDetalleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Actualizar(Guid zonaId, [FromBody] ActualizarZonaCoberturaRequest request,
        CancellationToken ct) => Ejecutar(async () =>
    {
        var zone = await zonas.ActualizarAsync(zonaId, request.Nombre, LeerGeometria(request.Geometria!), request.Version!.Value, ct);
        return zone is null ? NotFound(Error("No se encontró la zona.")) : Ok(ZonaCoberturaResponseMapper.Desde(zone));
    });

    [HttpPut("{zonaId:guid}/estado")]
    [ProducesResponseType<ZonaCoberturaDetalleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CambiarEstado(Guid zonaId, [FromBody] CambiarEstadoZonaRequest request,
        CancellationToken ct) => Ejecutar(async () =>
    {
        if (!TryEstadoZona(request.Estado, out var status))
            return RechazarBadRequest("Estado de zona inválido.", "El estado debe ser Activa o Inactiva.");
        var zone = await zonas.CambiarEstadoAsync(zonaId, status!.Value, request.Version!.Value, ct);
        return zone is null ? NotFound(Error("No se encontró la zona.")) : Ok(ZonaCoberturaResponseMapper.Desde(zone));
    });

    [HttpGet("{zonaId:guid}/franjas")]
    [ProducesResponseType<FranjasEntregaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ListarFranjas(Guid zonaId, CancellationToken ct) => Ejecutar(async () =>
    {
        var zone = await zonas.ListarFranjasAsync(zonaId, ct);
        return zone is null ? NotFound(Error("No se encontró la zona.")) : Ok(new FranjasEntregaResponse(
            zone.Franjas.OrderBy(x => x.DiaSemanaIso).ThenBy(x => x.HoraInicio)
                .Select(ZonaCoberturaResponseMapper.Desde).ToArray(), zone.Version, zone.RevisionActual));
    });

    [HttpPost("{zonaId:guid}/franjas")]
    [ProducesResponseType<FranjaEntregaCambioResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CrearFranja(Guid zonaId, [FromBody] CrearFranjaEntregaRequest request,
        CancellationToken ct) => Ejecutar(async () =>
    {
        var result = await zonas.CrearFranjaAsync(zonaId, (DiaSemanaIso)request.DiaSemanaIso!.Value,
            request.HoraInicio!.Value, request.HoraFin!.Value, request.VersionZona!.Value, ct: ct);
        return result is null ? NotFound(Error("No se encontró la zona."))
            : CreatedAtAction(nameof(ListarFranjas), new { zonaId }, new FranjaEntregaCambioResponse(
                ZonaCoberturaResponseMapper.Desde(result.Franja), result.VersionZona, result.RevisionActual));
    });

    [HttpPut("{zonaId:guid}/franjas/{franjaId:guid}")]
    [ProducesResponseType<FranjaEntregaCambioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ActualizarFranja(Guid zonaId, Guid franjaId,
        [FromBody] ActualizarFranjaEntregaRequest request, CancellationToken ct) => Ejecutar(async () =>
    {
        if (!TryEstadoFranja(request.Estado, out var status)) return BadRequest(Error("El estado debe ser Activa o Inactiva."));
        var result = await zonas.ActualizarFranjaAsync(zonaId, franjaId, (DiaSemanaIso)request.DiaSemanaIso!.Value,
            request.HoraInicio!.Value, request.HoraFin!.Value, status!.Value, request.VersionZona!.Value, ct);
        return result is null ? NotFound(Error("No se encontró la zona o franja."))
            : Ok(new FranjaEntregaCambioResponse(ZonaCoberturaResponseMapper.Desde(result.Franja),
                result.VersionZona, result.RevisionActual));
    });

    [HttpGet("departamentos")]
    [ProducesResponseType<IReadOnlyList<DepartamentoReferenciaResponse>>(StatusCodes.Status200OK)]
    public Task<IActionResult> ListarDepartamentos(CancellationToken ct) => Ejecutar(async () =>
    {
        var departments = await zonas.ListarDepartamentosAsync(ct);
        return Ok(departments.Select(x => new DepartamentoReferenciaResponse(x.Codigo, x.VersionFuente,
            x.Nombre, x.UrlFuente, x.Atribucion, x.Licencia, x.ImportadaUtc)).ToArray());
    });

    [HttpGet("departamentos/territorio")]
    [ProducesResponseType<TerritorioUruguayResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> ObtenerTerritorioUruguay(CancellationToken ct) => Ejecutar(async () =>
    {
        var territory = await zonas.ObtenerTerritorioUruguayAsync(ct);
        var bounds = new BoundsGeoJsonResponse(territory.Limites.MinX, territory.Limites.MinY,
            territory.Limites.MaxX, territory.Limites.MaxY);
        Response.Headers.CacheControl = "private, max-age=3600";
        Response.Headers.ETag = $"\"{territory.VersionFuente}\"";
        return Ok(new TerritorioUruguayResponse(territory.CodigoPais, territory.VersionFuente,
            ZonaCoberturaResponseMapper.GeometriaDesde(territory.Geometria), bounds));
    });

    [HttpGet("departamentos/{codigo}")]
    [ProducesResponseType<DepartamentoReferenciaGeometriaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ObtenerDepartamento(string codigo, CancellationToken ct) => Ejecutar(async () =>
    {
        var department = await zonas.ObtenerDepartamentoAsync(codigo, ct);
        if (department is null) return NotFound(Error("No se encontró el departamento en el catálogo local."));
        var bounds = new BoundsGeoJsonResponse(department.Limites.MinX, department.Limites.MinY,
            department.Limites.MaxX, department.Limites.MaxY);
        return Ok(new DepartamentoReferenciaGeometriaResponse(department.Codigo, department.VersionFuente,
            department.Nombre, ZonaCoberturaResponseMapper.GeometriaDesde(department.Geometria), bounds));
    });

    [HttpPost("cobertura")]
    [ProducesResponseType<ConsultaCoberturaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> ConsultarCobertura([FromBody] ConsultarCoberturaRequest request,
        CancellationToken ct) => Ejecutar(async () =>
    {
        var matches = await cobertura.EjecutarAsync(request.Longitud!.Value, request.Latitud!.Value, ct);
        var response = new ConsultaCoberturaResponse(matches.Count == 0 ? "SinCobertura" : "Cubierta",
            matches.Count > 1, matches.Select(x => new CoincidenciaCoberturaResponse(x.Id, x.Nombre,
                x.RevisionActual, x.Franjas.Where(f => f.Estado == EstadoFranjaEntrega.Activa)
                    .OrderBy(f => f.DiaSemanaIso).ThenBy(f => f.HoraInicio)
                    .Select(ZonaCoberturaResponseMapper.Desde).ToArray())).ToArray());
        return Ok(response);
    });

    private async Task<IActionResult> Ejecutar(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (AccesoCoberturaNoPermitidoException error)
        {
            RegistrarRechazo(StatusCodes.Status403Forbidden, error.GetType().Name, null);
            return Forbid();
        }
        catch (ConflictoConcurrenciaZonaException error)
        {
            RegistrarRechazo(StatusCodes.Status409Conflict, error.GetType().Name, error.Message);
            return Conflict(Error(error.Message));
        }
        catch (ConflictoConfiguracionZonaException error)
        {
            RegistrarRechazo(StatusCodes.Status409Conflict, error.GetType().Name, error.Message);
            return Conflict(Error(error.Message));
        }
        catch (ReferenciaTerritorialUruguayNoDisponibleException error)
        {
            RegistrarRechazo(StatusCodes.Status503ServiceUnavailable, error.GetType().Name, error.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Error(error.Message));
        }
        catch (GeometriaFueraDeUruguayException error)
        {
            RegistrarRechazo(StatusCodes.Status400BadRequest, error.GetType().Name, error.Message);
            return BadRequest(Error(error.Message));
        }
        catch (KeyNotFoundException error)
        {
            RegistrarRechazo(StatusCodes.Status404NotFound, error.GetType().Name, error.Message);
            return NotFound(Error(error.Message));
        }
        catch (ArgumentException error)
        {
            RegistrarRechazo(StatusCodes.Status400BadRequest, error.GetType().Name, error.Message);
            return BadRequest(Error(error.Message));
        }
        catch (Newtonsoft.Json.JsonException error)
        {
            const string message = "La geometría GeoJSON no tiene un formato válido.";
            RegistrarRechazo(StatusCodes.Status400BadRequest, error.GetType().Name, message);
            return BadRequest(Error(message));
        }
        catch (TopologyException error)
        {
            const string message = "La geometría GeoJSON no es válida.";
            RegistrarRechazo(StatusCodes.Status400BadRequest, error.GetType().Name, message);
            return BadRequest(Error(message));
        }
    }

    private IActionResult RechazarBadRequest(string reason, string message)
    {
        RegistrarRechazo(StatusCodes.Status400BadRequest, reason, message);
        return BadRequest(Error(message));
    }

    private void RegistrarRechazo(int statusCode, string reason, string? message)
    {
        var path = Request.Path.Value?.TrimEnd('/');
        var isCreate = HttpMethods.IsPost(Request.Method) &&
            string.Equals(path, "/api/configuracion/zonas", StringComparison.OrdinalIgnoreCase);
        var isActivation = HttpMethods.IsPut(Request.Method) &&
            path?.EndsWith("/estado", StringComparison.OrdinalIgnoreCase) == true;
        if (!isCreate && !isActivation) return;

        var traceId = HttpContext.TraceIdentifier;
        Response.Headers["X-Correlation-ID"] = traceId;
        var body = message is null ? "" : System.Text.Json.JsonSerializer.Serialize(new { message });
        logger.LogWarning(
            "Zone mutation rejected. TraceId={TraceId} Method={Method} Path={Path} StatusCode={StatusCode} Reason={Reason} ResponseBody={ResponseBody}",
            traceId, Request.Method, path, statusCode, reason, body);
    }

    private static Geometry LeerGeometria(GeoJsonGeometryRequest request)
    {
        if (request.Type is not ("Polygon" or "MultiPolygon") || request.Coordinates.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new ArgumentException("La geometría debe ser un objeto GeoJSON Polygon o MultiPolygon con coordenadas.");
        ValidarCoordenadas(request.Coordinates);
        var json = System.Text.Json.JsonSerializer.Serialize(new { type = request.Type, coordinates = request.Coordinates });
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        return new GeoJsonReader(factory, new JsonSerializerSettings()).Read<Geometry>(json);
    }

    private static void ValidarCoordenadas(System.Text.Json.JsonElement array)
    {
        if (array.ValueKind != System.Text.Json.JsonValueKind.Array || array.GetArrayLength() == 0)
            throw new ArgumentException("La geometría GeoJSON debe tener posiciones bidimensionales.");
        var values = array.EnumerateArray().ToArray();
        if (values[0].ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            if (values.Length != 2 || values.Any(x => x.ValueKind != System.Text.Json.JsonValueKind.Number || !x.TryGetDouble(out var coordinate) || !double.IsFinite(coordinate)))
                throw new ArgumentException("La geometría GeoJSON debe contener pares de coordenadas longitud/latitud.");
            var longitude = values[0].GetDouble();
            var latitude = values[1].GetDouble();
            if (longitude is < -180 or > 180 || latitude is < -90 or > 90)
                throw new ArgumentException("La geometría contiene coordenadas fuera del rango WGS 84.");
            return;
        }
        if (values.Any(x => x.ValueKind != System.Text.Json.JsonValueKind.Array))
            throw new ArgumentException("La geometría GeoJSON tiene una estructura de coordenadas inválida.");
        foreach (var child in values) ValidarCoordenadas(child);
    }

    private static bool TryEstadoZona(string? value, out EstadoZonaCobertura? state)
    {
        state = value switch { null or "" => null, "Activa" => EstadoZonaCobertura.Activa,
            "Inactiva" => EstadoZonaCobertura.Inactiva, _ => (EstadoZonaCobertura?)null };
        return value is null or "" or "Activa" or "Inactiva";
    }

    private static bool TryEstadoFranja(string value, out EstadoFranjaEntrega? state)
    {
        state = value switch { "Activa" => EstadoFranjaEntrega.Activa, "Inactiva" => EstadoFranjaEntrega.Inactiva, _ => null };
        return state is not null;
    }

    private static object Error(string message) => new { message };
}
