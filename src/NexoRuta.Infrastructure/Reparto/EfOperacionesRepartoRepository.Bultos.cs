using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.ReglasOperativas;
namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfOperacionesRepartoRepository
{
    private async Task<string?> AplicarResultadosBultos(HojaRuta hoja, OperacionRepartoDatos op, DatosOperacionReparto datos,
        Envio envio, ParadaRuta parada, Bulto[] propios, VersionReglasOperativas reglas, CancellationToken ct)
    {
        Exigir(hoja.SalidaEnUtc != null, "Confirmá la salida antes de visitar una parada.");
        if (op.Tipo == "RetiroFallido")
        {
            Exigir(envio.EsRetiro && parada.Activa && envio.Estado == EstadoEnvio.RetiroAsignado && op.VersionEnvio == envio.Version,
                "El retiro cambió o no pertenece a esta visita.");
            Exigir(reglas.Motivos.Any(m => m.Disponible && m.Codigo == datos.Motivo), "Seleccioná un motivo vigente del operador.");
            envio.CambiarEstado(EstadoEnvio.RetiroFallido, envio.Version); parada.Completar(); return "Retiro no realizado; disponible para otra visita.";
        }
        Exigir(datos.ResultadosBultos is { Length: > 0 }, "Identificá los bultos escaneados y sus versiones.");
        var resultados = datos.ResultadosBultos!;
        Exigir(resultados.All(r => parada.BultoIds.Length == 0 || parada.BultoIds.Contains(r.BultoId)), "El bulto no fue asignado a esta visita.");
        var seleccionados = resultados.Select(r => propios.SingleOrDefault(b => b.Id == r.BultoId)).ToArray();
        Exigir(seleccionados.All(b => b != null) && resultados.All(r => propios.Single(b => b.Id == r.BultoId).Version == r.Version),
            "Un bulto cambió; se conserva la operación como conflicto sin repetir efectos.");
        var evidencia = op.Tipo == "EntregaBultos" && resultados.All(r => propios.Single(b => b.Id == r.BultoId).Resultado == ResultadoBulto.EntregaPendienteValidacion);
        Exigir(parada.Activa || evidencia, "Esta parada ya fue completada o retirada de la ruta.");
        Exigir(datos.FotoId == null || await Archivo(hoja, envio.Id, datos.FotoId, "Foto", ct), "La foto no corresponde a esta visita.");
        Exigir(datos.FirmaId == null || await Archivo(hoja, envio.Id, datos.FirmaId, "Firma", ct), "La firma no corresponde a esta visita.");
        if (op.Tipo == "RetirarBultos")
        {
            Exigir(envio.EsRetiro && envio.Estado == EstadoEnvio.RetiroAsignado && resultados.Length == propios.Length
                && resultados.All(r => r.Entregado) && propios.All(b => b.Custodia == EstadoCustodiaBulto.SinRecepcion)
                && datos.FotoId != null && datos.FirmaId != null, "El retiro completo requiere identificar todos los bultos, foto de su estado y firma.");
            var carga = await db.Bultos.Where(b => b.OperadorId == hoja.OperadorId && b.RutaCustodiaId == hoja.Id
                && (b.Custodia == EstadoCustodiaBulto.Repartidor || b.Custodia == EstadoCustodiaBulto.PendienteDeposito)).ToArrayAsync(ct);
            var vehiculo = await db.Vehiculos.SingleAsync(v => v.OperadorId == hoja.OperadorId && v.Id == hoja.VehiculoId, ct);
            Exigir(CapacidadCarga.Exceso(vehiculo, carga.Concat(propios)) == null, "El retiro excede la capacidad física actual del vehículo.");
            foreach (var bulto in propios)
            {
                await PruebaPorBulto(hoja, envio.Id, bulto.Id, datos, ct);
                bulto.Retirar(hoja.RepartidorAccesoId, hoja.Id);
            }
            envio.CambiarEstado(EstadoEnvio.Retirado, envio.Version); parada.Completar(); return "Retiro registrado; custodia del repartidor hasta la recepción física.";
        }
        Exigir(!envio.EsRetiro && envio.Estado is EstadoEnvio.EnTransito or EstadoEnvio.EntregadoParcialmente or EstadoEnvio.EntregaPendienteValidacion,
            "El envío no admite resultados de entrega.");
        var intentos = await IntentosPorBulto.Calcular(db, hoja.OperadorId, envio.Id, propios, reglas.IntervaloEntreIntentosMinutos!.Value, ct);
        if (!evidencia)
        {
            var pendientes = propios.Where(b => b.Resultado == ResultadoBulto.Pendiente).ToArray();
            Exigir(resultados.Length == pendientes.Length && resultados.All(r => pendientes.Any(b => b.Id == r.BultoId)),
                "Informá el resultado de cada bulto pendiente de esta visita.");
            Exigir(reglas.PermiteEntregaParcial || resultados.All(r => r.Entregado), "El operador no admite entrega parcial.");
            Exigir(resultados.Any(r => r.Entregado), "Usá intento fallido cuando no se entrega ningún bulto.");
            foreach (var r in resultados)
            {
                var b = propios.Single(b => b.Id == r.BultoId);
                intentos[b.Id].ExigirSiguienteIntento(op.OcurridaEn, reglas.MaxIntentosEntrega!.Value);
                Exigir(r.Entregado ? b.Custodia == EstadoCustodiaBulto.Repartidor && b.RutaCustodiaId == hoja.Id && b.AccesoCustodioId == hoja.RepartidorAccesoId
                    : reglas.Motivos.Any(m => m.Disponible && m.Codigo == r.Motivo), "La entrega exige custodia; cada pendiente exige un motivo del catálogo.");
            }
        }
        // Validate all inputs before mutations. A failed command retains its immutable payload/evidence in the existing journal.
        foreach (var r in resultados)
        {
            var b = propios.Single(b => b.Id == r.BultoId);
            if (r.Entregado)
            {
                var prueba = await PruebaPorBulto(hoja, envio.Id, b.Id, datos, ct);
                if (evidencia)
                {
                    Exigir(b.RutaCustodiaId == hoja.Id && b.Custodia == EstadoCustodiaBulto.Destinatario, "La prueba pertenece a otra entrega física.");
                    if (Completa(prueba, reglas) && (b.PruebaVenceEnUtc == null || reloj.GetUtcNow() < b.PruebaVenceEnUtc))
                        b.RegistrarResultado(ResultadoBulto.Entregado, b.Version);
                }
                else
                {
                    b.Entregar(hoja.RepartidorAccesoId, hoja.Id);
                    b.RegistrarResultado(Completa(prueba, reglas) ? ResultadoBulto.Entregado : ResultadoBulto.EntregaPendienteValidacion,
                        b.Version, entregadoEn: op.OcurridaEn, vence: reloj.GetUtcNow().AddMinutes(reglas.PlazoValidacionPruebaMinutos!.Value));
                }
            }
            else if (reglas.PoliticaPendientesParcial == PoliticaPendientesParcial.DevolverComercio
                || intentos[b.Id].Cantidad + 1 >= reglas.MaxIntentosEntrega && reglas.PoliticaDevolucionAlAgotarIntentos == PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente)
                b.RegistrarResultado(ResultadoBulto.EnDevolucion, b.Version, r.Motivo);
            else b.RegistrarPendiente(r.Motivo!, b.Version);
        }
        envio.ActualizarResultadoBultos(propios, reloj.GetUtcNow()); parada.Completar();
        return $"{propios.Count(b => b.Resultado == ResultadoBulto.Entregado)} de {propios.Length} bultos entregados; pendientes según la política fijada al admitir.";
    }
    private async Task<string?> AplicarIntentoParcial(HojaRuta hoja, OperacionRepartoDatos op, DatosOperacionReparto datos,
        Envio envio, ParadaRuta parada, Bulto[] propios, VersionReglasOperativas reglas, ConfiguracionReparto politica, CancellationToken ct)
    {
        Exigir(hoja.SalidaEnUtc != null && parada.Activa && op.VersionEnvio == envio.Version, "La visita parcial cambió o ya no está activa.");
        Exigir(reglas.Motivos.Any(m => m.Disponible && m.Codigo == datos.Motivo), "Seleccioná un motivo de la publicación del envío.");
        Exigir(!politica.RequiereFotoIntento || await Archivo(hoja, envio.Id, datos.FotoId, "Foto", ct), "El intento requiere una foto verificada.");
        var pendientes = propios.Where(b => b.Resultado == ResultadoBulto.Pendiente && (parada.BultoIds.Length == 0 || parada.BultoIds.Contains(b.Id))).ToArray();
        Exigir(pendientes.Length > 0 && pendientes.All(b => b.Custodia == EstadoCustodiaBulto.Repartidor && b.RutaCustodiaId == hoja.Id && b.AccesoCustodioId == hoja.RepartidorAccesoId), "El intento requiere los pendientes cargados en esta visita.");
        var intentos = await IntentosPorBulto.Calcular(db, hoja.OperadorId, envio.Id, propios, reglas.IntervaloEntreIntentosMinutos!.Value, ct);
        foreach (var b in pendientes) intentos[b.Id].ExigirSiguienteIntento(op.OcurridaEn, reglas.MaxIntentosEntrega!.Value);
        foreach (var b in pendientes)
            if (reglas.PoliticaPendientesParcial == PoliticaPendientesParcial.DevolverComercio
                || intentos[b.Id].Cantidad + 1 >= reglas.MaxIntentosEntrega && reglas.PoliticaDevolucionAlAgotarIntentos == PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente)
                b.RegistrarResultado(ResultadoBulto.EnDevolucion, b.Version, datos.Motivo);
            else b.RegistrarPendiente(datos.Motivo!, b.Version);
        envio.ActualizarResultadoBultos(propios, reloj.GetUtcNow()); parada.Completar();
        return "Intento registrado por bulto; se conserva la entrega parcial y la custodia de los pendientes.";
    }
    private async Task<PruebaEntrega> PruebaPorBulto(HojaRuta hoja, Guid envio, Guid bulto, DatosOperacionReparto d, CancellationToken ct)
    {
        var p = await db.PruebasEntrega.SingleOrDefaultAsync(p => p.OperadorId == hoja.OperadorId && p.EnvioId == envio && p.BultoId == bulto, ct);
        Exigir(p == null || p.RutaId == hoja.Id, "El bulto ya tiene prueba en otra ruta.");
        if (p == null) { p = new() { OperadorId = hoja.OperadorId, EnvioId = envio, BultoId = bulto, RutaId = hoja.Id }; db.Add(p); }
        p.FotoId = d.FotoId ?? p.FotoId; p.FirmaId = d.FirmaId ?? p.FirmaId;
        p.Nombre = string.IsNullOrWhiteSpace(d.Nombre) ? p.Nombre : d.Nombre.Trim();
        p.Documento = string.IsNullOrWhiteSpace(d.Documento) ? p.Documento : d.Documento.Trim();
        if (d.Latitud != null && d.Longitud != null && d.PrecisionMetros != null && d.PosicionEnUtc != null)
        { p.Latitud = d.Latitud; p.Longitud = d.Longitud; p.PrecisionMetros = d.PrecisionMetros; p.PosicionEnUtc = d.PosicionEnUtc; }
        return p;
    }
}
