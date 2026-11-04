using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;

namespace NexoRuta.Application.Reparto.UseCases;

public sealed class ConfigurarRepartoUseCase(IUsuarioActual usuarioActual, IConfiguracionRepartoRepository repository, TimeProvider reloj, NexoRuta.Application.Envios.Interfaces.IGeocodificador geocodificador)
{
    public async Task<BaseOperativaDatos?> BaseAsync(CancellationToken ct) => await repository.BaseAsync(await Operador(ct), ct);
    public async Task<BaseOperativaDatos> GuardarBaseAsync(GuardarBaseOperativaDatos datos, CancellationToken ct) => await repository.GuardarBaseAsync(await Operador(ct), datos, ct);
    public async Task<BaseOperativaDatos?> ConfirmarBaseAsync(long version, double latitud, double longitud, CancellationToken ct)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        if (actor.OperadorId is null || actor.ComercioId is not null) throw new RepartoNoPermitidoException();
        return await repository.ConfirmarBaseAsync(actor, version, latitud, longitud, reloj.GetUtcNow(), ct);
    }
    public async Task<PropuestasBaseDatos?> ProponerBaseAsync(CancellationToken ct)
    {
        var actual = await BaseAsync(ct); if (actual is null) return null;
        return new(actual.Version, actual.Direccion, geocodificador.Atribucion, await geocodificador.BuscarAsync(actual.Direccion, ct));
    }
    private async Task<Guid> Operador(CancellationToken ct) =>
        (await usuarioActual.ObtenerAsync(ct)).OperadorId ?? throw new RepartoNoPermitidoException();
    public async Task<ConfiguracionRepartoDatos?> ObtenerAsync(CancellationToken ct) => await repository.ObtenerAsync(await Operador(ct), ct);
    public async Task<ConfiguracionRepartoDatos> GuardarAsync(ConfiguracionRepartoDatos datos, CancellationToken ct) =>
        await repository.GuardarAsync(await Operador(ct), datos, ct);
    public async Task<IReadOnlyList<VehiculoDatos>> VehiculosAsync(CancellationToken ct) => await repository.VehiculosAsync(await Operador(ct), ct);
    public async Task<VehiculoDatos?> GuardarVehiculoAsync(VehiculoDatos datos, CancellationToken ct) =>
        await repository.GuardarVehiculoAsync(await Operador(ct), datos, ct);
}
