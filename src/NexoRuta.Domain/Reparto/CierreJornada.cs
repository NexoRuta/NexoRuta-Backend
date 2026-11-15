using NexoRuta.Domain.Envios;
namespace NexoRuta.Domain.Reparto;

public static class CierreJornada
{
    public static string? Finalizar(HojaRuta hoja, IReadOnlyCollection<Envio> envios,
        IReadOnlyCollection<Bulto> bultos, DateTimeOffset instante)
    {
        var asignadosSinSalida = envios.Count(e => e.Estado == EstadoEnvio.AsignadoRuta);
        foreach (var parada in hoja.Paradas.Where(x => x.Activa))
        {
            var envio = envios.Single(e => e.Id == parada.EnvioId);
            if (envio.Estado == EstadoEnvio.AsignadoRuta)
                hoja.SuspenderParada(envio.Id, "Jornada cerrada antes de la salida; requiere reasignación operativa.");
            else if (parada.ResultadoVisita == "Pendiente")
                hoja.SuspenderParada(envio.Id, "Jornada cerrada sin completar esta visita.");
        }
        hoja.RegistrarFin(instante);
        var faltantes = bultos.Count(b => b.RutaCustodiaId == hoja.Id && b.Custodia == EstadoCustodiaBulto.Repartidor);
        var avisos = new List<string>();
        if (asignadosSinSalida > 0) avisos.Add($"{asignadosSinSalida} envío(s) permanecen asignados sin salida y requieren reasignación operativa.");
        if (faltantes > 0) avisos.Add($"{faltantes} bulto(s) siguen sin entrega ni rendición. Se conserva su custodia; no se confirmó recepción en depósito.");
        return avisos.Count == 0 ? null : $"Jornada cerrada. {string.Join(" ", avisos)}";
    }
}
