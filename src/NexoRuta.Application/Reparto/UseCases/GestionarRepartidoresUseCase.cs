using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;

namespace NexoRuta.Application.Reparto.UseCases;

public sealed class GestionarRepartidoresUseCase(IUsuarioActual usuarioActual, IRepartidoresRepository repository)
{
    public async Task<IReadOnlyList<RepartidorResumen>> ListarAsync(CancellationToken ct)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        return await repository.ListarAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), ct);
    }

    public async Task<RepartidorResumen?> GuardarAsync(Guid accesoId, string nombre, bool activo, long? version, CancellationToken ct)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        return await repository.GuardarAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(),
            accesoId, nombre, activo, version, ct);
    }

    public async Task<RepartidorResumen?> PerfilAsync(CancellationToken ct)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        return await repository.ObtenerActivoAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), actor.AccesoId, ct);
    }
}
