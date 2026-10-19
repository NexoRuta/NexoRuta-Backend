using NetTopologySuite.Geometries;
using NexoRuta.Application.ZonasCobertura.Repositories;
using NexoRuta.Domain.ZonasCobertura;

namespace NexoRuta.Application.ZonasCobertura.UseCases;

public sealed record FranjaCoberturaResultado(FranjaEntrega Franja, long VersionZona, int RevisionActual);

public sealed class GestionarZonasCoberturaUseCase(
    RequerirOperadorCoberturaUseCase acceso,
    CrearZonaCoberturaUseCase crear,
    IZonasCoberturaRepository zonas,
    ValidarTerritorioUruguayUseCase territorio)
{
    public async Task<(IReadOnlyList<ZonaCoberturaResumen> Items, int Total)> ListarAsync(
        EstadoZonaCobertura? estado, int pagina, int tamanoPagina, CancellationToken ct = default)
    {
        await acceso.EjecutarAsync(ct);
        if (pagina is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(pagina));
        if (tamanoPagina is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(tamanoPagina));
        return await zonas.ListarAsync(estado, pagina, tamanoPagina, ct);
    }

    public async Task<ZonaCobertura?> ObtenerAsync(Guid zonaId, CancellationToken ct = default)
    {
        await acceso.EjecutarAsync(ct);
        return await zonas.ObtenerAsync(zonaId, ct);
    }

    public async Task<Guid> CrearAsync(string nombre, Geometry geometria, CancellationToken ct = default)
    {
        return await crear.EjecutarAsync(nombre, geometria, cancellationToken: ct);
    }

    public async Task<Guid> CrearDesdeDepartamentoAsync(string nombre, string codigo, CancellationToken ct = default)
    {
        return await crear.DesdeDepartamentoAsync(nombre, codigo, ct);
    }

    public async Task<ZonaCobertura?> ActualizarAsync(
        Guid zonaId, string nombre, Geometry geometria, long version, CancellationToken ct = default)
    {
        var actor = await acceso.EjecutarAsync(ct);
        var zona = await zonas.ObtenerAsync(zonaId, ct);
        if (zona is null) return null;
        if (zona.CambiarConfiguracion(nombre, geometria, version))
        {
            await territorio.VerificarAsync(zona.Geometria, ct);
            await zonas.GuardarCambioAsync(zona, version, actor.UsuarioId, ct);
        }
        return zona;
    }

    public async Task<ZonaCobertura?> CambiarEstadoAsync(
        Guid zonaId, EstadoZonaCobertura estado, long version, CancellationToken ct = default)
    {
        var actor = await acceso.EjecutarAsync(ct);
        var zona = await zonas.ObtenerAsync(zonaId, ct);
        if (zona is null) return null;
        var cambio = estado switch
        {
            EstadoZonaCobertura.Activa => zona.Activar(version),
            EstadoZonaCobertura.Inactiva => zona.Desactivar(version),
            _ => throw new ArgumentOutOfRangeException(nameof(estado))
        };
        if (cambio)
        {
            if (estado == EstadoZonaCobertura.Activa) await territorio.VerificarAsync(zona.Geometria, ct);
            await zonas.GuardarCambioAsync(zona, version, actor.UsuarioId, ct);
        }
        return zona;
    }

    public async Task<ZonaCobertura?> ListarFranjasAsync(Guid zonaId, CancellationToken ct = default)
    {
        await acceso.EjecutarAsync(ct);
        return await zonas.ObtenerAsync(zonaId, ct);
    }

    public async Task<FranjaCoberturaResultado?> CrearFranjaAsync(
        Guid zonaId, DiaSemanaIso dia, TimeOnly inicio, TimeOnly fin, long version,
        EstadoFranjaEntrega estado = EstadoFranjaEntrega.Activa, CancellationToken ct = default)
    {
        var actor = await acceso.EjecutarAsync(ct);
        var zona = await zonas.ObtenerAsync(zonaId, ct);
        if (zona is null) return null;
        var franja = zona.AgregarFranja(dia, inicio, fin, version, estado);
        await zonas.GuardarCambioAsync(zona, version, actor.UsuarioId, ct);
        return new(franja, zona.Version, zona.RevisionActual);
    }

    public async Task<FranjaCoberturaResultado?> ActualizarFranjaAsync(
        Guid zonaId, Guid franjaId, DiaSemanaIso dia, TimeOnly inicio, TimeOnly fin,
        EstadoFranjaEntrega estado, long version, CancellationToken ct = default)
    {
        var actor = await acceso.EjecutarAsync(ct);
        var zona = await zonas.ObtenerAsync(zonaId, ct);
        if (zona is null) return null;
        if (zona.CambiarFranja(franjaId, dia, inicio, fin, estado, version))
            await zonas.GuardarCambioAsync(zona, version, actor.UsuarioId, ct);
        return new(zona.Franjas.Single(x => x.Id == franjaId), zona.Version, zona.RevisionActual);
    }

    public async Task<IReadOnlyList<DepartamentoReferenciaResumen>> ListarDepartamentosAsync(CancellationToken ct = default)
    {
        await acceso.EjecutarAsync(ct);
        return await zonas.ListarDepartamentosAsync(ct);
    }

    public async Task<TerritorioUruguayReferencia> ObtenerTerritorioUruguayAsync(CancellationToken ct = default)
    {
        await acceso.EjecutarAsync(ct);
        return await territorio.ObtenerAsync(ct);
    }

    public Task<DepartamentoReferenciaConGeometria?> ObtenerDepartamentoAsync(string codigo, CancellationToken ct = default)
        => crear.ObtenerDepartamentoAsync(codigo, ct);
}
