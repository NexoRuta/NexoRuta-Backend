using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class EditarNombreOrganizacionPropiaUseCase(
    IUsuarioActual usuarioActual,
    IOrganizacionPropiaRepository organizaciones,
    EsPropietarioOrganizacionUseCase propiedad)
{
    public async Task<OrganizacionPropiaActual> EjecutarAsync(string nombre, long version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 160)
            throw new ArgumentException("El nombre es obligatorio y no puede superar los 160 caracteres.", nameof(nombre));

        var actor = await usuarioActual.ObtenerAsync(ct);
        var id = ConsultarOrganizacionPropiaUseCase.RequerirOrganizacionId(actor);
        var esPropietario = await propiedad.EjecutarAsync(ct);
        if (!esPropietario)
        {
            if (!await organizaciones.TieneDuenoRegistradoAsync(actor.TipoAcceso, id, ct))
                throw new OrganizacionSinDuenoException();
            throw new AccesoNoPermitidoException();
        }

        var actualizada = await organizaciones.ActualizarNombreAsync(actor.TipoAcceso, id, nombre.Trim(), version, ct)
            ?? throw new OrganizacionActualNoEncontradaException();
        return new(actualizada.Id, actualizada.Nombre, actualizada.Version, actualizada.Estado, true, true);
    }
}
