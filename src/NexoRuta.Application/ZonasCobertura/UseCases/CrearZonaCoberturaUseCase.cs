using NetTopologySuite.Geometries;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Application.ZonasCobertura.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ZonasCobertura;

namespace NexoRuta.Application.ZonasCobertura.UseCases;

public sealed class CrearZonaCoberturaUseCase(
    RequerirOperadorCoberturaUseCase acceso,
    IZonasCoberturaRepository zonas,
    ValidarTerritorioUruguayUseCase territorio)
{
    public async Task<Guid> EjecutarAsync(
        string nombre,
        Geometry geometria,
        OrigenGeometriaZona origenGeometria = OrigenGeometriaZona.Dibujada,
        string? codigoDepartamento = null,
        string? versionFuente = null,
        IEnumerable<FranjaEntregaInicial>? franjasIniciales = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await acceso.EjecutarAsync(cancellationToken);

        var zone = ZonaCobertura.Crear(actor.OperadorId!.Value, nombre, geometria, origenGeometria,
            codigoDepartamento, versionFuente, franjasIniciales: franjasIniciales);
        await territorio.VerificarAsync(zone.Geometria, cancellationToken);
        await zonas.GuardarNuevaAsync(zone, actor.UsuarioId, cancellationToken);
        return zone.Id;
    }

    public async Task<Guid> DesdeDepartamentoAsync(string nombre, string codigo, CancellationToken cancellationToken = default)
    {
        var actor = await acceso.EjecutarAsync(cancellationToken);
        var department = await zonas.ObtenerDepartamentoConGeometriaAsync(codigo, cancellationToken)
            ?? throw new KeyNotFoundException("No existe el código de departamento en el catálogo local.");
        var zone = ZonaCobertura.Crear(actor.OperadorId!.Value, nombre, department.Geometria,
            OrigenGeometriaZona.Departamento, department.Codigo, department.VersionFuente);
        await territorio.VerificarAsync(zone.Geometria, cancellationToken);
        await zonas.GuardarNuevaAsync(zone, actor.UsuarioId, cancellationToken);
        return zone.Id;
    }

    public async Task<DepartamentoReferenciaConGeometria?> ObtenerDepartamentoAsync(string codigo, CancellationToken cancellationToken = default)
    {
        await acceso.EjecutarAsync(cancellationToken);
        return await zonas.ObtenerDepartamentoConGeometriaAsync(codigo, cancellationToken);
    }
}
