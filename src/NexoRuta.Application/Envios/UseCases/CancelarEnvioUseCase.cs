using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Application.Reparto.Exceptions;

namespace NexoRuta.Application.Envios.UseCases;

public sealed class CancelarEnvioUseCase(IUsuarioActual usuario, IPermisosUsuarioRepository permisos, ICancelacionesEnvioRepository cancelaciones)
{
    public async Task<EnvioCancelado?> EjecutarAsync(Guid envioId, long version, string motivo, CancellationToken ct)
    {
        if (envioId == Guid.Empty || version < 0 || string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500)
            throw new ArgumentException("Indicá el envío, su versión y un motivo de hasta 500 caracteres.");
        var actor = await usuario.ObtenerAsync(ct);
        var permiso = actor.ComercioId.HasValue ? "envios.crear" : "envios.gestionar";
        if (actor.OperadorId.HasValue && actor.RolCodigo != "administrador_operador"
            || !await permisos.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, permiso, ct))
            throw new RepartoNoPermitidoException();
        return await cancelaciones.CancelarAsync(actor, envioId, version, motivo.Trim(), ct);
    }
}
