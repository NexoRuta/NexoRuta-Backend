using System.Globalization;

namespace NexoRuta.Domain.Envios;

public static class MedidasBulto
{
    // Texto invariante para DataAnnotations/OpenAPI; los límites numéricos se derivan de él.
    public const string MinimoTexto = "0.01";
    public const string MaximoTexto = "999999999";
    public static decimal Minimo { get; } = decimal.Parse(MinimoTexto, CultureInfo.InvariantCulture);
    public static decimal Maximo { get; } = decimal.Parse(MaximoTexto, CultureInfo.InvariantCulture);

    public static bool EstaEnRango(decimal valor) => valor >= Minimo && valor <= Maximo;

    // La precisión se puede reutilizar sin imponer el rango propio de los bultos.
    public static bool TieneHastaDosDecimalesEfectivos(decimal valor) => decimal.Round(valor, 2) == valor;

    public static bool EsValida(decimal valor) => EstaEnRango(valor) && TieneHastaDosDecimalesEfectivos(valor);
}
