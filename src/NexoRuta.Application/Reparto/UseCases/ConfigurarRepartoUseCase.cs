using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;

namespace NexoRuta.Application.Reparto.UseCases;

public sealed class ConfigurarRepartoUseCase(IUsuarioActual usuarioActual, IConfiguracionRepartoRepository repository)
{
    private async Task<Guid> Operador(CancellationToken ct) =>
        (await usuarioActual.ObtenerAsync(ct)).OperadorId ?? throw new RepartoNoPermitidoException();
    public async Task<ConfiguracionRepartoDatos?> ObtenerAsync(CancellationToken ct) => await repository.ObtenerAsync(await Operador(ct), ct);
    public async Task<ConfiguracionRepartoDatos> GuardarAsync(ConfiguracionRepartoDatos datos, CancellationToken ct) =>
        await repository.GuardarAsync(await Operador(ct), datos, ct);
    public async Task<IReadOnlyList<VehiculoDatos>> VehiculosAsync(CancellationToken ct) => await repository.VehiculosAsync(await Operador(ct), ct);
    public async Task<VehiculoDatos?> GuardarVehiculoAsync(VehiculoDatos datos, CancellationToken ct) =>
        await repository.GuardarVehiculoAsync(await Operador(ct), datos, ct);
}
