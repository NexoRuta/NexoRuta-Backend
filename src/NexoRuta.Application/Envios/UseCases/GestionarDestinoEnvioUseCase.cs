using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Interfaces;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Reparto.Exceptions;

namespace NexoRuta.Application.Envios.UseCases;

public sealed class GestionarDestinoEnvioUseCase(IUsuarioActual usuario, IDestinosEnvioRepository destinos, IGeocodificador geocodificador)
{
    public async Task<PropuestasDestinoDatos> BuscarParaAltaAsync(string consulta, CancellationToken ct)
    {
        (await usuario.ObtenerAsync(ct)).RequerirComercio();
        var propuestas = await geocodificador.BuscarAsync(consulta, ct, autocompletar: true);
        return new(Guid.Empty, consulta, 0, geocodificador.Atribucion, propuestas);
    }

    public async Task<DestinoEnvioDatos?> ObtenerAsync(Guid envioId, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await destinos.ObtenerAsync(actor.ComercioId ?? throw new RepartoNoPermitidoException(), envioId, ct);
    }
    public async Task<PropuestasDestinoDatos?> ProponerAsync(Guid envioId, CancellationToken ct, string? consulta = null)
    {
        var destino = await ObtenerAsync(envioId, ct);
        if (destino is null) return null;
        if (destino.Estado != "Admitido" && !(destino.Estado == "EnDeposito" && destino.ConfirmadaEnUtc is null))
            throw new ConflictoRepartoException("El destino pendiente puede confirmarse en depósito; una ubicación confirmada no puede cambiarse después de la recepción.");
        var propuestas = await geocodificador.BuscarAsync(consulta ?? destino.Direccion, ct, autocompletar: consulta is not null);
        // La propuesta no escribe coordenadas: solo la confirmación expresa puede hacerlo.
        return new(envioId, destino.Direccion, destino.Version, geocodificador.Atribucion, propuestas);
    }
    public async Task<bool> ConfirmarAsync(Guid envioId, long version, double latitud, double longitud, CancellationToken ct, string? direccion = null)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await destinos.ConfirmarAsync(actor.ComercioId ?? throw new RepartoNoPermitidoException(), actor.UsuarioId,
            envioId, version, latitud, longitud, ct, direccion);
    }
}
