using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfOperacionesRepartoRepository
{
    private async Task<string?> Ejecutar(HojaRuta hoja, OperacionRepartoDatos op, DatosOperacionReparto datos, CancellationToken ct)
    {
        var politica = await db.ConfiguracionesReparto.SingleAsync(x => x.OperadorId == hoja.OperadorId && x.Version == hoja.ConfiguracionVersion, ct);
        if (op.Tipo is "InicioJornada" or "CargarBulto" or "ConfirmarCarga")
        {
            var ocupacion = await db.OcupacionesRuta.SingleOrDefaultAsync(o => o.OperadorId == hoja.OperadorId && o.RutaId == hoja.Id
                && o.VehiculoId == hoja.VehiculoId && o.RepartidorAccesoId == hoja.RepartidorAccesoId
                && o.LiberadaEnUtc == null && o.AnuladaEnUtc == null, ct);
            Exigir(ocupacion is not null && ocupacion.AdquiridaEnUtc is not null && ocupacion.RetornoEnUtc is null,
                "La ruta no conserva una ocupación autorizada para iniciar, cargar o salir; verificá su conciliación en Backoffice.");
        }
        if (op.Tipo == "InicioJornada")
        {
            Exigir(hoja.Estado == EstadoHojaRuta.Despachada, "La hoja ya no está lista para iniciar.");
            var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(politica.ZonaHoraria)).DateTime);
            // An authorized occupation survives midnight and delayed offline synchronization.
            Exigir(hoja.Fecha <= hoy, "La jornada nueva debe iniciarse con la hoja correspondiente al día del operador.");
            hoja.RegistrarInicio(reloj.GetUtcNow()); return null;
        }
        Exigir(hoja.Estado == EstadoHojaRuta.EnCurso
            || hoja.Estado == EstadoHojaRuta.Finalizada && op.Tipo is "EvidenciaEntrega" or "EntregaBultos",
            "La jornada ya no admite esta operación.");
        if (op.Tipo == "Posicion")
        {
            Exigir(datos.Latitud is not null && datos.Longitud is not null && datos.PrecisionMetros is not null && datos.PosicionEnUtc is not null,
                "La posición requiere coordenadas, precisión y fecha medidas por el dispositivo.");
            return null;
        }
        var envioIds = hoja.Paradas.Select(x => x.EnvioId).ToArray();
        var bultos = await db.Bultos.Where(x => x.OperadorId == hoja.OperadorId && envioIds.Contains(x.EnvioId)).ToArrayAsync(ct);
        var envios = await db.Envios.Where(x => x.OperadorId == hoja.OperadorId && envioIds.Contains(x.Id)).ToArrayAsync(ct);
        if (op.Tipo == "ConfirmarCarga")
        {
            Exigir(hoja.SalidaEnUtc is null, "La carga ya fue confirmada; no se admiten cargas intermedias.");
            var reglasCarga = await db.VersionesReglasOperativas.Where(v => v.OperadorId == hoja.OperadorId).ToDictionaryAsync(v => v.Id, ct);
            var incompletos = hoja.Paradas.Where(p => p.Activa && !envios.Single(e => e.Id == p.EnvioId).EsRetiro
                && !(reglasCarga[envios.Single(e => e.Id == p.EnvioId).VersionReglasId].PermiteEntregaParcial
                    && bultos.Any(b => b.EnvioId == p.EnvioId && b.Resultado == ResultadoBulto.Pendiente && b.Custodia == EstadoCustodiaBulto.Repartidor && b.RutaCustodiaId == hoja.Id))
                && bultos.Where(b => b.EnvioId == p.EnvioId && b.Resultado == ResultadoBulto.Pendiente)
                .Any(b => b.Custodia != EstadoCustodiaBulto.Repartidor || b.AccesoCustodioId != hoja.RepartidorAccesoId || b.RutaCustodiaId != hoja.Id)).ToArray();
            Exigir(!politica.BloquearDiscrepanciasCarga || incompletos.Length == 0, "Hay diferencias de carga pendientes de resolver en depósito.");
            var vehiculo = await db.Vehiculos.SingleAsync(v => v.OperadorId == hoja.OperadorId && v.Id == hoja.VehiculoId, ct);
            Exigir(vehiculo.Activo, "El vehículo está deshabilitado; no se puede confirmar la salida.");
            // Suspender una parada no descarga sus bultos: validar toda la carga física de esta hoja antes de mutar estados.
            var cargados = bultos.Where(b => b.Custodia is EstadoCustodiaBulto.Repartidor or EstadoCustodiaBulto.PendienteDeposito
                && b.AccesoCustodioId == hoja.RepartidorAccesoId && b.RutaCustodiaId == hoja.Id);
            if (CapacidadCarga.Exceso(vehiculo, cargados) is { } exceso) throw new ConflictoRepartoException(exceso);
            Exigir(hoja.Paradas.Where(p => p.Activa && !incompletos.Contains(p))
                .All(p => envios.Single(e => e.Id == p.EnvioId).Estado is EstadoEnvio.AsignadoRuta or EstadoEnvio.EntregadoParcialmente or EstadoEnvio.RetiroAsignado),
                "Un envío cambió durante la preparación de carga.");
            Exigir(hoja.Paradas.Count(p => p.Activa) <= politica.MaximoParadas, "La ruta supera el máximo de paradas configurado.");
            foreach (var parada in hoja.Paradas.Where(p => p.Activa && !incompletos.Contains(p)))
                await RestriccionesTemporales.ValidarAsync(db, hoja.OperadorId, parada.EnvioId, parada.LlegadaEstimadaUtc, parada.ServicioMinutos, ct);
            foreach (var parada in incompletos)
            {
                hoja.SuspenderParada(parada.EnvioId, "Carga incompleta: requiere revisión en depósito; no se cambió el estado comercial.");
            }
            foreach (var parada in hoja.Paradas.Where(p => p.Activa))
            {
                var envio = envios.Single(x => x.Id == parada.EnvioId);
                if (!envio.EsRetiro) envio.CambiarEstado(EstadoEnvio.EnTransito, envio.Version);
            }
            hoja.ConfirmarSalida(op.OcurridaEn);
            return incompletos.Length == 0 ? null : $"Salida con {incompletos.Length} envío(s) suspendidos por diferencias de carga.";
        }
        if (op.Tipo == "Rendicion")
        {
            Exigir(datos.BultoIds is { Length: > 0 }, "La rendición debe identificar los bultos que se devuelven.");
            var seleccionados = bultos.Where(x => datos.BultoIds!.Contains(x.Id)).ToArray();
            Exigir(seleccionados.Length == datos.BultoIds!.Length && seleccionados.All(x => x.AccesoCustodioId == hoja.RepartidorAccesoId
                && x.RutaCustodiaId == hoja.Id && x.Custodia == EstadoCustodiaBulto.Repartidor), "La rendición no coincide con los bultos bajo custodia del repartidor.");
            foreach (var bulto in seleccionados) bulto.DeclararRendicion(hoja.RepartidorAccesoId, hoja.Id);
            foreach (var envio in envios.Where(e => seleccionados.Any(b => b.EnvioId == e.Id) && e.Estado is EstadoEnvio.EnTransito or EstadoEnvio.NoEntregado))
            {
                if (hoja.Paradas.Any(p => p.EnvioId == envio.Id && p.Activa))
                    hoja.SuspenderParada(envio.Id, envio.Estado == EstadoEnvio.NoEntregado
                        ? "Intento fallido; se requiere reprogramación explícita. Recepción pendiente en depósito."
                        : "Devuelto por el repartidor; recepción pendiente en depósito.");
            }
            return "Rendición declarada. El depósito debe confirmar la recepción física.";
        }
        if (op.Tipo == "FinJornada")
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
            hoja.RegistrarFin(op.OcurridaEn);
            var faltantes = bultos.Count(b => b.RutaCustodiaId == hoja.Id && b.Custodia == EstadoCustodiaBulto.Repartidor);
            var avisos = new List<string>();
            if (asignadosSinSalida > 0) avisos.Add($"{asignadosSinSalida} envío(s) permanecen asignados sin salida y requieren reasignación operativa.");
            if (faltantes > 0) avisos.Add($"{faltantes} bulto(s) siguen sin entrega ni rendición. Se conserva su custodia; no se confirmó recepción en depósito.");
            return avisos.Count == 0 ? null : $"Jornada cerrada. {string.Join(" ", avisos)}";
        }
        var actual = envios.SingleOrDefault(x => x.Id == op.EnvioId);
        var paradaActual = hoja.Paradas.SingleOrDefault(x => x.EnvioId == op.EnvioId);
        Exigir(actual is not null && paradaActual is not null, "El envío no pertenece a esta hoja.");
        var reglas = await db.VersionesReglasOperativas.Include(v => v.RequisitosPrueba).Include(v => v.Motivos)
            .SingleAsync(v => v.OperadorId == hoja.OperadorId && v.Id == actual!.VersionReglasId
                && v.Estado == EstadoVersionReglasOperativas.Publicada, ct);
        var propios = bultos.Where(x => x.EnvioId == actual!.Id).ToArray();
        if (op.Tipo == "IntentoFallido" && actual!.Estado == EstadoEnvio.EntregadoParcialmente)
            return await AplicarIntentoParcial(hoja, op, datos, actual, paradaActual!, propios, reglas, politica, ct);
        if (op.Tipo is "EntregaBultos" or "RetirarBultos" or "RetiroFallido")
            return await AplicarResultadosBultos(hoja, op, datos, actual!, paradaActual!, propios, reglas, ct);
        if (op.Tipo == "EvidenciaEntrega")
            return await AplicarEvidenciaEntrega(hoja, op, datos, actual!, propios, reglas, ct);
        if (op.Tipo == "EntregaFisica" && op.VersionEnvio is >= 0 && op.VersionEnvio < actual!.Version
            && actual.Estado == EstadoEnvio.Cancelado)
        {
            Exigir(hoja.SalidaEnUtc is not null && propios.Length > 0 && propios.All(x => x.Custodia == EstadoCustodiaBulto.Repartidor
                && x.AccesoCustodioId == hoja.RepartidorAccesoId && x.RutaCustodiaId == hoja.Id),
                "La entrega física no coincide con los bultos cargados y su custodia; se conserva como conflicto.");
            var otras = await db.HojasRuta.Include(h => h.Paradas).Where(h => h.OperadorId == hoja.OperadorId && h.Id != hoja.Id
                && h.Paradas.Any(p => p.EnvioId == actual.Id && p.Activa)).ToArrayAsync(ct);
            Exigir(otras.All(h => h.Estado == EstadoHojaRuta.Planificada), "La asignación posterior ya fue despachada; se conserva el hecho como conflicto.");
            var prueba = await AplicarPrueba(hoja, actual.Id, datos, ct);
            var recibidoEnUtc = HoraServidorUtc();
            actual.ConciliarEntregaFisica(op.OcurridaEn, recibidoEnUtc, reglas.PlazoValidacionPruebaMinutos,
                Completa(prueba, reglas), actual.Version);
            foreach (var bulto in propios)
            {
                bulto.Entregar(hoja.RepartidorAccesoId, hoja.Id);
                bulto.RegistrarResultado(Completa(prueba, reglas) ? ResultadoBulto.Entregado : ResultadoBulto.EntregaPendienteValidacion,
                    bulto.Version, entregadoEn: op.OcurridaEn, vence: actual.PlazoPruebaVenceEnUtc);
            }
            foreach (var otra in otras) otra.SuspenderParada(actual.Id, "Entrega física registrada en la ruta original; no corresponde otra entrega.");
            paradaActual!.Completar();
            return "Entrega física conciliada; el envío conserva el estado Cancelado y su prueba conserva los requisitos fijados al crearlo.";
        }
        Exigir(op.VersionEnvio == actual!.Version, "El envío cambió en el servidor; se conservan el hecho y su evidencia como conflicto de sincronización.");
        if (op.Tipo == "CargarBulto")
        {
            Exigir(hoja.SalidaEnUtc is null && paradaActual!.Activa && !actual.EsRetiro && actual.Estado is EstadoEnvio.AsignadoRuta or EstadoEnvio.EntregadoParcialmente, "La carga solo se realiza en depósito antes de salir.");
            var bulto = propios.SingleOrDefault(x => x.Id == datos.BultoId);
            Exigir(bulto is not null && bulto.Custodia == EstadoCustodiaBulto.Deposito && bulto.Resultado == ResultadoBulto.Pendiente, "El bulto no está asignado a esta parada o no fue recibido en depósito.");
            bulto!.Cargar(hoja.RepartidorAccesoId, hoja.Id); return null;
        }
        Exigir(hoja.SalidaEnUtc is not null, "Confirmá la carga antes de registrar entregas o intentos.");
        Exigir(paradaActual!.Activa && actual.Estado == EstadoEnvio.EnTransito && propios.All(x => x.Custodia == EstadoCustodiaBulto.Repartidor
            && x.AccesoCustodioId == hoja.RepartidorAccesoId && x.RutaCustodiaId == hoja.Id), "La entrega fue suspendida, cambió de estado o tiene otra custodia.");
        if (op.Tipo == "IntentoFallido")
        {
            Exigir(datos.Motivo is not null && reglas.Motivos.Any(m => m.Codigo == datos.Motivo && m.Disponible), "El motivo no pertenece a las reglas de este envío.");
            Exigir(!politica.RequiereFotoIntento || await Archivo(hoja, actual.Id, datos.FotoId, "Foto", ct), "El intento requiere una foto subida y verificada.");
            var anteriores = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == hoja.OperadorId
                && x.EnvioId == actual.Id && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada").Select(x => x.Payload).ToArrayAsync(ct);
            ResumenIntentosEntrega.Calcular(anteriores, reglas.IntervaloEntreIntentosMinutos!.Value)
                .ExigirSiguienteIntento(op.OcurridaEn, reglas.MaxIntentosEntrega!.Value);
            foreach (var b in propios.Where(b => b.Resultado == ResultadoBulto.Pendiente)) b.RegistrarPendiente(datos.Motivo!, b.Version);
            actual.CambiarEstado(EstadoEnvio.NoEntregado, actual.Version);
            if (anteriores.Length + 1 == reglas.MaxIntentosEntrega!.Value
                && reglas.PoliticaDevolucionAlAgotarIntentos == PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente)
            {
                // Keep both authorized edges visible to the database history trigger;
                // the surrounding request transaction still commits or rolls back the whole command.
                await db.SaveChangesAsync(ct);
                foreach (var b in propios.Where(b => b.Resultado == ResultadoBulto.Pendiente))
                    b.RegistrarResultado(ResultadoBulto.EnDevolucion, b.Version, datos.Motivo);
                actual.CambiarEstado(EstadoEnvio.EnDevolucion, actual.Version);
                hoja.SuspenderParada(actual.Id, "Intentos agotados: devolución iniciada; requiere rendición y recepción en depósito.");
                return "Intentos agotados; devolución iniciada sin modificar la custodia física.";
            }
            return null;
        }
        if (op.Tipo == "EntregaFisica")
        {
            var anteriores = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == hoja.OperadorId
                && x.EnvioId == actual.Id && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada").Select(x => x.Payload).ToArrayAsync(ct);
            ResumenIntentosEntrega.Calcular(anteriores, reglas.IntervaloEntreIntentosMinutos!.Value)
                .ExigirSiguienteIntento(op.OcurridaEn, reglas.MaxIntentosEntrega!.Value);
            var prueba = await AplicarPrueba(hoja, actual.Id, datos, ct);
            var recibidoEnUtc = HoraServidorUtc();
            actual.DeclararEntregaFisica(op.OcurridaEn, recibidoEnUtc, reglas.PlazoValidacionPruebaMinutos,
                Completa(prueba, reglas), actual.Version);
            foreach (var bulto in propios)
            {
                bulto.Entregar(hoja.RepartidorAccesoId, hoja.Id);
                bulto.RegistrarResultado(Completa(prueba, reglas) ? ResultadoBulto.Entregado : ResultadoBulto.EntregaPendienteValidacion,
                    bulto.Version, entregadoEn: op.OcurridaEn, vence: actual.PlazoPruebaVenceEnUtc);
            }
            paradaActual.Completar();
            return actual.Estado == EstadoEnvio.Entregado ? "Entrega validada." : "Entrega física declarada; la prueba queda pendiente según las reglas de esta versión.";
        }
        throw new InvalidOperationException("El tipo validado no tiene un procesador de reparto.");
    }

    private async Task<string?> AplicarEvidenciaEntrega(HojaRuta hoja, OperacionRepartoDatos op,
        DatosOperacionReparto datos, Envio actual, Bulto[] propios, VersionReglasOperativas reglas, CancellationToken ct)
    {
        Exigir(hoja.SalidaEnUtc is not null, "Confirmá la carga antes de registrar evidencia de entrega.");
        _ = await db.Envios.FromSqlInterpolated($"SELECT * FROM public.\"Envios\" WHERE \"OperadorId\"={hoja.OperadorId} AND \"Id\"={actual.Id} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        await db.Entry(actual).ReloadAsync(ct);
        var recibidaEnUtc = HoraServidorUtc();

        var originalRegistrada = await db.OperacionesJornada.AsNoTracking().AnyAsync(x => x.OperadorId == hoja.OperadorId
            && x.AccesoId == hoja.RepartidorAccesoId && x.RutaId == hoja.Id && x.EnvioId == actual.Id
            && x.Tipo == "EntregaFisica" && x.Resultado == "Aplicada", ct);
        Exigir(originalRegistrada && actual.EntregaFisicaEnUtc is not null && propios.Length > 0 && propios.All(x =>
            x.Custodia == EstadoCustodiaBulto.Destinatario && x.AccesoCustodioId == hoja.RepartidorAccesoId
            && x.RutaCustodiaId == hoja.Id),
            "La prueba requiere la entrega física original y su custodia en destino desde esta ruta.");

        var versionActual = actual.Version;
        var esReintentoPosteriorAlVencimiento = actual.Estado == EstadoEnvio.EntregaSinValidacion
            && op.VersionEnvio is { } versionOperacion && versionOperacion < versionActual
            && versionActual == versionOperacion + 1
            && await db.EnvioEventos.AsNoTracking().AnyAsync(x => x.OperadorId == hoja.OperadorId && x.EnvioId == actual.Id
                && x.Tipo == "TransicionEstado" && x.EstadoAnterior == nameof(EstadoEnvio.EntregaPendienteValidacion)
                && x.EstadoNuevo == nameof(EstadoEnvio.EntregaSinValidacion) && x.OrigenTipo == "PlazoValidacionPrueba"
                && x.VersionAnterior == versionOperacion && x.VersionNueva == versionActual, ct);
        Exigir(op.VersionEnvio == versionActual || esReintentoPosteriorAlVencimiento,
            "El envío cambió en el servidor; se conservan el hecho y su evidencia como conflicto de sincronización.");

        var prueba = await AplicarPrueba(hoja, actual.Id, datos, ct);
        if (actual.Estado == EstadoEnvio.Cancelado)
        {
            Exigir(op.VersionEnvio == versionActual, "La prueba tardía requiere la versión vigente del envío cancelado.");
            actual.RegistrarPruebaTardiaTrasCancelacion(versionActual);
            return "Prueba de entrega tardía registrada; el envío conserva el estado Cancelado.";
        }
        if (actual.Estado == EstadoEnvio.EntregaPendienteValidacion)
        {
            var validada = actual.RegistrarEvidenciaEntrega(recibidaEnUtc, Completa(prueba, reglas), versionActual);
            if (validada)
            {
                foreach (var b in propios.Where(b => b.Resultado == ResultadoBulto.EntregaPendienteValidacion))
                    b.RegistrarResultado(ResultadoBulto.Entregado, b.Version);
                return "Prueba completa; entrega validada automáticamente.";
            }
            return actual.PlazoPruebaVenceEnUtc is { } vence && recibidaEnUtc >= vence
                ? "La prueba se conservó como evidencia; el plazo venció y el Worker resolverá el estado sin validar."
                : "La entrega conserva prueba pendiente.";
        }
        if (actual.Estado == EstadoEnvio.EntregaSinValidacion)
        {
            actual.RegistrarEvidenciaTardiaSinTransicion(versionActual);
            return "Prueba recibida después del vencimiento y conservada como evidencia; el estado no se reabrió.";
        }
        throw new ConflictoRepartoException("La entrega ya no admite esta prueba en su estado actual.");
    }

    private DateTimeOffset HoraServidorUtc() => reloj.GetUtcNow();

    private async Task<PruebaEntrega> AplicarPrueba(HojaRuta hoja, Guid envioId, DatosOperacionReparto datos, CancellationToken ct)
    {
        Exigir(datos.FotoId is null || await Archivo(hoja, envioId, datos.FotoId, "Foto", ct), "La foto no está subida o no pertenece a esta entrega.");
        Exigir(datos.FirmaId is null || await Archivo(hoja, envioId, datos.FirmaId, "Firma", ct), "La firma no está subida o no pertenece a esta entrega.");
        var prueba = await db.PruebasEntrega.SingleOrDefaultAsync(x => x.OperadorId == hoja.OperadorId && x.EnvioId == envioId && x.BultoId == Guid.Empty, ct);
        Exigir(prueba is null || prueba.RutaId == hoja.Id, "La prueba pertenece a otra ruta; se conserva la evidencia como conflicto de sincronización.");
        if (prueba is null) { prueba = new() { OperadorId = hoja.OperadorId, EnvioId = envioId, RutaId = hoja.Id }; db.PruebasEntrega.Add(prueba); }
        if (!string.IsNullOrWhiteSpace(datos.Nombre)) prueba.Nombre = datos.Nombre.Trim();
        if (!string.IsNullOrWhiteSpace(datos.Documento)) prueba.Documento = datos.Documento.Trim();
        if (datos.FotoId is not null) prueba.FotoId = datos.FotoId;
        if (datos.FirmaId is not null) prueba.FirmaId = datos.FirmaId;
        if (datos.Latitud is not null && datos.Longitud is not null && datos.PrecisionMetros is not null && datos.PosicionEnUtc is not null)
        { prueba.Latitud = datos.Latitud; prueba.Longitud = datos.Longitud; prueba.PrecisionMetros = datos.PrecisionMetros; prueba.PosicionEnUtc = datos.PosicionEnUtc.Value.ToUniversalTime(); }
        return prueba;
    }
    private Task<bool> Archivo(HojaRuta hoja, Guid envio, Guid? id, string tipo, CancellationToken ct) =>
        db.EvidenciasReparto.AnyAsync(x => x.OperadorId == hoja.OperadorId && x.RutaId == hoja.Id && x.AccesoId == hoja.RepartidorAccesoId
            && x.EnvioId == envio && x.Id == id && x.Tipo == tipo, ct);
    private static bool Completa(PruebaEntrega p, VersionReglasOperativas reglas) => p.Latitud is not null && p.Longitud is not null
        && p.PrecisionMetros is not null && p.PosicionEnUtc is not null && reglas.RequisitosPrueba.All(r => r.Tipo switch
        {
            TipoPruebaEntrega.NombreReceptor => !string.IsNullOrWhiteSpace(p.Nombre),
            TipoPruebaEntrega.DocumentoReceptor => !string.IsNullOrWhiteSpace(p.Documento),
            TipoPruebaEntrega.Fotografia => p.FotoId is not null,
            TipoPruebaEntrega.FirmaEnPantalla => p.FirmaId is not null,
            _ => false
        });
}
