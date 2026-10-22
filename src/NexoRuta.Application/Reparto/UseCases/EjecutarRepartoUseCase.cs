using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;
namespace NexoRuta.Application.Reparto.UseCases;
public sealed class EjecutarRepartoUseCase(IUsuarioActual usuario, IRepartidoresRepository repartidores, IOperacionesRepartoRepository operaciones)
{
    private async Task<(Guid Op, Guid Acceso)> Actor(CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        var op = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        if (await repartidores.ObtenerActivoAsync(op, actor.AccesoId, ct) is null) throw new RepartoNoPermitidoException();
        return (op, actor.AccesoId);
    }
    public async Task<ResultadoOperacionReparto?> AplicarAsync(Guid jornadaId, OperacionRepartoDatos datos, CancellationToken ct)
    { var actor = await Actor(ct); return await operaciones.AplicarAsync(actor.Op, actor.Acceso, jornadaId, datos, ct); }
    public async Task<bool> EvidenciaAsync(Guid jornadaId, Guid envioId, Guid archivoId, string tipo, byte[] contenido, CancellationToken ct)
    { var actor = await Actor(ct); return await operaciones.GuardarEvidenciaAsync(actor.Op, actor.Acceso, jornadaId, envioId, archivoId, tipo, contenido, ct); }
}
