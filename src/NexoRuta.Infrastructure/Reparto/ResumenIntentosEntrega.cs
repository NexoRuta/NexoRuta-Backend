using System.Text.Json;
using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Infrastructure.Reparto;

internal sealed record ResumenIntentosEntrega(int Cantidad, DateTimeOffset? UltimoIntentoUtc,
    DateTimeOffset? ProximoIntentoElegibleUtc)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ResumenIntentosEntrega Calcular(IEnumerable<string> payloads, int intervaloMinutos)
    {
        var intentos = payloads.Select(payload => JsonSerializer.Deserialize<OperacionRepartoDatos>(payload, Json)
                ?? throw new InvalidOperationException("Una operación de reparto persistida no es válida."))
            .Select(operacion => operacion.OcurridaEn.ToUniversalTime()).ToArray();
        var ultimo = intentos.Length == 0 ? (DateTimeOffset?)null : intentos.Max();
        DateTimeOffset? proximo = null;
        if (ultimo is { } instante)
        {
            try { proximo = instante.AddMinutes(intervaloMinutos); }
            catch (ArgumentOutOfRangeException) { }
        }
        return new(intentos.Length, ultimo, proximo);
    }

    public void ExigirSiguienteIntento(DateTimeOffset ocurridoEn, int maximoIntentos)
    {
        if (Cantidad >= maximoIntentos)
            throw new NexoRuta.Application.Reparto.Exceptions.ConflictoRepartoException(
                "Se alcanzó el máximo de intentos configurado para este envío.");
        if (ProximoIntentoElegibleUtc is { } proximo && ocurridoEn.ToUniversalTime() < proximo)
            throw new NexoRuta.Application.Reparto.Exceptions.ConflictoRepartoException(
                $"La nueva tentativa no puede ocurrir antes de {proximo:O} según las reglas de este envío.");
    }
}
