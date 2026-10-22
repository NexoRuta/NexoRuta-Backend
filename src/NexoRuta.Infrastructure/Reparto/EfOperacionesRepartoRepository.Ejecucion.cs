using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfOperacionesRepartoRepository
{
    private async Task<string?> Ejecutar(HojaRuta hoja, OperacionRepartoDatos op, DatosOperacionReparto datos, CancellationToken ct)
    {
        var politica = await db.ConfiguracionesReparto.SingleAsync(x => x.OperadorId == hoja.OperadorId && x.Version == hoja.ConfiguracionVersion, ct);
        if (op.Tipo == "InicioJornada")
        {
            Exigir(hoja.Estado == EstadoHojaRuta.Despachada, "La hoja ya no está lista para iniciar.");
            var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(politica.ZonaHoraria)).DateTime);
            Exigir(hoja.Fecha == hoy, "La jornada nueva debe iniciarse con la hoja correspondiente al día del operador.");
            hoja.RegistrarInicio(reloj.GetUtcNow()); return null;
        }
        Exigir(hoja.Estado == EstadoHojaRuta.EnCurso, "La jornada ya no está en curso.");
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
            var incompletos = hoja.Paradas.Where(p => p.Activa && bultos.Where(b => b.EnvioId == p.EnvioId)
                .Any(b => b.Custodia != EstadoCustodiaBulto.Repartidor || b.AccesoCustodioId != hoja.RepartidorAccesoId || b.RutaCustodiaId != hoja.Id)).ToArray();
            Exigir(!politica.BloquearDiscrepanciasCarga || incompletos.Length == 0, "Hay diferencias de carga pendientes de resolver en depósito.");
            foreach (var parada in incompletos)
            {
                var envio = envios.Single(x => x.Id == parada.EnvioId);
                Exigir(envio.Estado == EstadoEnvio.AsignadoRuta, "Un envío cambió durante la preparación de carga.");
                envio.CambiarEstado(bultos.Any(b => b.EnvioId == envio.Id && b.Custodia == EstadoCustodiaBulto.Repartidor)
                    ? EstadoEnvio.Reprogramado : EstadoEnvio.EnDeposito, envio.Version);
                hoja.SuspenderParada(envio.Id, "Carga incompleta: requiere revisión en depósito.");
            }
            foreach (var parada in hoja.Paradas.Where(p => p.Activa))
            {
                var envio = envios.Single(x => x.Id == parada.EnvioId);
                Exigir(envio.Estado == EstadoEnvio.AsignadoRuta, "Un envío cambió durante la preparación de carga.");
                envio.CambiarEstado(EstadoEnvio.EnTransito, envio.Version);
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
                envio.CambiarEstado(EstadoEnvio.Reprogramado, envio.Version);
                if (hoja.Paradas.Any(p => p.EnvioId == envio.Id && p.Activa)) hoja.SuspenderParada(envio.Id, "Devuelto por el repartidor; recepción pendiente en depósito.");
            }
            return "Rendición declarada. El depósito debe confirmar la recepción física.";
        }
        if (op.Tipo == "FinJornada")
        {
            foreach (var envio in envios.Where(x => x.Estado == EstadoEnvio.EntregaPendienteValidacion
                && bultos.Any(b => b.EnvioId == x.Id && b.RutaCustodiaId == hoja.Id && b.Custodia == EstadoCustodiaBulto.Destinatario)))
                envio.CambiarEstado(EstadoEnvio.EntregaSinValidacion, envio.Version);
            foreach (var parada in hoja.Paradas.Where(x => x.Activa))
            {
                var envio = envios.Single(e => e.Id == parada.EnvioId);
                if (envio.Estado == EstadoEnvio.AsignadoRuta && bultos.Where(b => b.EnvioId == envio.Id)
                    .All(b => b.Custodia == EstadoCustodiaBulto.Deposito)) envio.CambiarEstado(EstadoEnvio.EnDeposito, envio.Version);
                parada.Completar();
            }
            hoja.RegistrarFin(op.OcurridaEn);
            var faltantes = bultos.Count(b => b.RutaCustodiaId == hoja.Id && b.Custodia == EstadoCustodiaBulto.Repartidor);
            return faltantes == 0 ? null : $"Jornada cerrada con incidencia: {faltantes} bulto(s) sin entrega ni rendición. Se conserva su custodia; no se confirmó recepción en depósito.";
        }
        var actual = envios.SingleOrDefault(x => x.Id == op.EnvioId);
        var paradaActual = hoja.Paradas.SingleOrDefault(x => x.EnvioId == op.EnvioId);
        Exigir(actual is not null && paradaActual is not null, "El envío no pertenece a esta hoja.");
        var reglas = await db.VersionesReglasOperativas.Include(v => v.RequisitosPrueba).Include(v => v.Motivos)
            .SingleAsync(v => v.OperadorId == hoja.OperadorId && v.Id == actual!.VersionReglasId
                && v.Estado == EstadoVersionReglasOperativas.Publicada, ct);
        var propios = bultos.Where(x => x.EnvioId == actual!.Id).ToArray();
        if (op.Tipo == "EntregaFisica" && op.VersionEnvio is >= 0 && op.VersionEnvio < actual!.Version
            && actual.Estado is (EstadoEnvio.Cancelado or EstadoEnvio.Reprogramado or EstadoEnvio.AsignadoRuta))
        {
            Exigir(hoja.SalidaEnUtc is not null && propios.Length > 0 && propios.All(x => x.Custodia == EstadoCustodiaBulto.Repartidor
                && x.AccesoCustodioId == hoja.RepartidorAccesoId && x.RutaCustodiaId == hoja.Id),
                "La entrega física no coincide con los bultos cargados y su custodia; se conserva como conflicto.");
            var otras = await db.HojasRuta.Include(h => h.Paradas).Where(h => h.OperadorId == hoja.OperadorId && h.Id != hoja.Id
                && h.Paradas.Any(p => p.EnvioId == actual.Id && p.Activa)).ToArrayAsync(ct);
            Exigir(otras.All(h => h.Estado == EstadoHojaRuta.Planificada), "La asignación posterior ya fue despachada; se conserva el hecho como conflicto.");
            var prueba = await AplicarPrueba(hoja, actual.Id, datos, ct);
            actual.ConciliarEntregaFisica(op.OcurridaEn, Completa(prueba, reglas), actual.Version);
            foreach (var bulto in propios) bulto.Entregar(hoja.RepartidorAccesoId, hoja.Id);
            foreach (var otra in otras) otra.SuspenderParada(actual.Id, "Entrega física registrada en la ruta original; no corresponde otra entrega.");
            paradaActual!.Completar();
            return "Entrega física conciliada automáticamente; una cancelación posterior requiere devolución. La prueba conserva los requisitos fijados al crear el envío.";
        }
        Exigir(op.VersionEnvio == actual!.Version, "El envío cambió en el servidor; se conservan el hecho y su evidencia como conflicto de sincronización.");
        if (op.Tipo == "CargarBulto")
        {
            Exigir(hoja.SalidaEnUtc is null && paradaActual!.Activa && actual.Estado == EstadoEnvio.AsignadoRuta, "La carga solo se realiza en depósito antes de salir.");
            var bulto = propios.SingleOrDefault(x => x.Id == datos.BultoId);
            Exigir(bulto is not null && bulto.Custodia == EstadoCustodiaBulto.Deposito, "El bulto no está asignado a esta parada o no fue recibido en depósito.");
            bulto!.Cargar(hoja.RepartidorAccesoId, hoja.Id); return null;
        }
        Exigir(hoja.SalidaEnUtc is not null, "Confirmá la carga antes de registrar entregas o intentos.");
        if (op.Tipo == "EvidenciaEntrega")
        {
            Exigir(actual.Estado == EstadoEnvio.EntregaPendienteValidacion && actual.EntregaFisicaEnUtc is not null,
                "La entrega no está pendiente de prueba dentro de esta jornada.");
            var prueba = await AplicarPrueba(hoja, actual.Id, datos, ct);
            if (Completa(prueba, reglas)) actual.CambiarEstado(EstadoEnvio.Entregado, actual.Version);
            else actual.RegistrarProgresoPrueba(actual.Version);
            return actual.Estado == EstadoEnvio.Entregado ? "Prueba completa; entrega validada automáticamente." : "La entrega conserva prueba pendiente.";
        }
        Exigir(paradaActual!.Activa && actual.Estado is (EstadoEnvio.EnTransito or EstadoEnvio.NoEntregado) && propios.All(x => x.Custodia == EstadoCustodiaBulto.Repartidor
            && x.AccesoCustodioId == hoja.RepartidorAccesoId && x.RutaCustodiaId == hoja.Id), "La entrega fue suspendida, cambió de estado o tiene otra custodia.");
        if (op.Tipo == "IntentoFallido")
        {
            Exigir(datos.Motivo is not null && reglas.Motivos.Any(m => m.Codigo == datos.Motivo && m.Disponible), "El motivo no pertenece a las reglas de este envío.");
            Exigir(!politica.RequiereFotoIntento || await Archivo(hoja, actual.Id, datos.FotoId, "Foto", ct), "El intento requiere una foto subida y verificada.");
            var anteriores = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == hoja.OperadorId
                && x.EnvioId == actual.Id && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada").Select(x => x.Payload).ToArrayAsync(ct);
            Exigir(anteriores.Length < reglas.MaxIntentosEntrega!.Value, "Se alcanzó el máximo de intentos configurado.");
            var ultima = anteriores.Select(p => JsonSerializer.Deserialize<OperacionRepartoDatos>(p, Json)!.OcurridaEn)
                .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            Exigir(anteriores.Length == 0 || op.OcurridaEn - ultima >= TimeSpan.FromMinutes(reglas.IntervaloEntreIntentosMinutos!.Value),
                "No se cumplió el intervalo entre intentos de este envío; se conserva el registro como conflicto.");
            if (actual.Estado == EstadoEnvio.NoEntregado) actual.CambiarEstado(EstadoEnvio.EnTransito, actual.Version);
            actual.CambiarEstado(EstadoEnvio.NoEntregado, actual.Version);
            if (anteriores.Length + 1 == reglas.MaxIntentosEntrega!.Value
                && reglas.PoliticaDevolucionAlAgotarIntentos == PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente)
            {
                actual.CambiarEstado(EstadoEnvio.EnDevolucion, actual.Version);
                hoja.SuspenderParada(actual.Id, "Intentos agotados: devolución iniciada; requiere rendición y recepción en depósito.");
                return "Intentos agotados; devolución iniciada sin modificar la custodia física.";
            }
            return null;
        }
        if (op.Tipo == "EntregaFisica")
        {
            var prueba = await AplicarPrueba(hoja, actual.Id, datos, ct);
            if (actual.Estado == EstadoEnvio.NoEntregado) actual.CambiarEstado(EstadoEnvio.EnTransito, actual.Version);
            actual.DeclararEntregaFisica(op.OcurridaEn, Completa(prueba, reglas), actual.Version);
            foreach (var bulto in propios) bulto.Entregar(hoja.RepartidorAccesoId, hoja.Id);
            paradaActual.Completar();
            return actual.Estado == EstadoEnvio.Entregado ? "Entrega validada." : "Entrega física declarada; prueba pendiente hasta cerrar la jornada.";
        }
        throw new InvalidOperationException("El tipo validado no tiene un procesador de reparto.");
    }

    private async Task<PruebaEntrega> AplicarPrueba(HojaRuta hoja, Guid envioId, DatosOperacionReparto datos, CancellationToken ct)
    {
        Exigir(datos.FotoId is null || await Archivo(hoja, envioId, datos.FotoId, "Foto", ct), "La foto no está subida o no pertenece a esta entrega.");
        Exigir(datos.FirmaId is null || await Archivo(hoja, envioId, datos.FirmaId, "Firma", ct), "La firma no está subida o no pertenece a esta entrega.");
        var prueba = await db.PruebasEntrega.SingleOrDefaultAsync(x => x.OperadorId == hoja.OperadorId && x.EnvioId == envioId, ct);
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
