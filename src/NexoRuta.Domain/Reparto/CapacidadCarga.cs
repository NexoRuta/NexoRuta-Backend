using System.Globalization;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Domain.Reparto;

public static class CapacidadCarga
{
    public static string? ExcesoRecorrido(Vehiculo vehiculo, IEnumerable<(Guid EnvioId, bool Retiro)> orden,
        IEnumerable<Bulto> bultos)
    {
        var pasos = orden.ToArray(); var todos = bultos.Where(b => b.Resultado == ResultadoBulto.Pendiente).ToArray();
        var retiros = pasos.Where(p => p.Retiro).Select(p => p.EnvioId).ToHashSet();
        var carga = todos.Where(b => !retiros.Contains(b.EnvioId)).ToList();
        if (Exceso(vehiculo, carga) is { } inicial) return "Salida: " + inicial;
        foreach (var paso in pasos)
        {
            if (paso.Retiro) carga.AddRange(todos.Where(b => b.EnvioId == paso.EnvioId));
            else carga.RemoveAll(b => b.EnvioId == paso.EnvioId);
            if (Exceso(vehiculo, carga) is { } exceso) return "Después de la parada: " + exceso;
        }
        return null;
    }
    public static string? Exceso(Vehiculo vehiculo, IEnumerable<Bulto> bultos)
    {
        decimal peso = 0, volumen = 0;
        var excesoPeso = false;
        var excesoVolumen = false;
        foreach (var bulto in bultos.DistinctBy(b => b.Id))
        {
            if (!excesoPeso)
            {
                excesoPeso = bulto.PesoGramos > vehiculo.PesoMaximoGramos - peso;
                if (!excesoPeso) peso += bulto.PesoGramos;
            }
            if (!excesoVolumen)
            {
                // ponytail: bounded accumulation needs no unrepresentable total; historical DB values may exceed current input limits.
                try
                {
                    var parcial = bulto.LargoCentimetros * bulto.AnchoCentimetros * bulto.AltoCentimetros;
                    excesoVolumen = parcial > vehiculo.VolumenMaximoCm3 - volumen;
                    if (!excesoVolumen) volumen += parcial;
                }
                catch (OverflowException) { excesoVolumen = true; }
            }
            if (excesoPeso && excesoVolumen) break;
        }
        if (!excesoPeso && !excesoVolumen) return null;
        var magnitud = excesoPeso && excesoVolumen ? "peso y volumen" : excesoPeso ? "peso" : "volumen";
        return $"La carga supera la capacidad vigente de {magnitud} del vehículo. Límites: {vehiculo.PesoMaximoGramos.ToString(CultureInfo.InvariantCulture)} g y {vehiculo.VolumenMaximoCm3.ToString(CultureInfo.InvariantCulture)} cm³.";
    }
}
