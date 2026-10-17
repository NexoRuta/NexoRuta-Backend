using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed record OrganizacionPropiaActual(Guid Id, string Nombre, long Version, EstadoOrganizacion Estado, bool EsPropietario, bool TieneDuenoRegistrado);

public sealed class ConsultarOrganizacionPropiaUseCase(
    IUsuarioActual usuarioActual,
    IOrganizacionPropiaRepository organizaciones,
    EsPropietarioOrganizacionUseCase propiedad)
{
    public async Task<OrganizacionPropiaActual> EjecutarAsync(CancellationToken ct = default)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        var organizacion = await organizaciones.ObtenerAsync(actor.TipoAcceso, RequerirOrganizacionId(actor), ct)
            ?? throw new OrganizacionActualNoEncontradaException();
        var esPropietario = await propiedad.EjecutarAsync(ct);
        var tieneDueno = esPropietario || await organizaciones.TieneDuenoRegistradoAsync(actor.TipoAcceso, organizacion.Id, ct);
        return new(organizacion.Id, organizacion.Nombre, organizacion.Version, organizacion.Estado, esPropietario, tieneDueno);
    }

    internal static Guid RequerirOrganizacionId(ContextoUsuario actor)
    {
        if (actor.OperadorId is { } operadorId && actor.ComercioId is null && operadorId != Guid.Empty)
            return operadorId;
        if (actor.ComercioId is { } comercioId && actor.OperadorId is null && comercioId != Guid.Empty)
            return comercioId;
        throw new AccesoNoPermitidoException();
    }
}
