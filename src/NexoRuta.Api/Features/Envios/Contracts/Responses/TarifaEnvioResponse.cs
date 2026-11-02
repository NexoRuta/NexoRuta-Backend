namespace NexoRuta.Api.Features.Envios.Contracts.Responses;
public sealed record TarifaEnvioResponse(Guid VersionTarifarioId, Guid ZonaId, string Modalidad, string Moneda, decimal TotalFinalUyu, decimal ImporteBaseUyu, decimal ComponentePesoUyu, decimal ComponenteVolumenUyu, decimal TotalRecargosUyu, decimal TotalBonificacionesUyu)
{
    public static TarifaEnvioResponse Desde(NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa tarifa)
        => new(tarifa.VersionTarifarioId!.Value, tarifa.ZonaId, tarifa.Modalidad.ToString(), tarifa.Moneda.ToString(), tarifa.TotalFinalUyu, tarifa.ImporteBaseUyu, tarifa.ComponentePesoUyu, tarifa.ComponenteVolumenUyu, tarifa.TotalRecargosUyu, tarifa.TotalBonificacionesUyu);
}
