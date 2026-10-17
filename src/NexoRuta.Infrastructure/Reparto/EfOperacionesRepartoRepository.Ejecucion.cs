using Microsoft.EntityFrameworkCore;
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
            Exigir(!bultos.Any(x => x.RutaCustodiaId == hoja.Id && x.Custodia == EstadoCustodiaBulto.Repartidor), "Declará primero los bultos que devolvés al depósito.");
            foreach (var envio in envios.Where(x => x.Estado == EstadoEnvio.EntregaPendienteValidacion))
                envio.CambiarEstado(EstadoEnvio.EntregaSinValidacion, envio.Version);
            foreach (var parada in hoja.Paradas.Where(x => x.Activa))
            {
                var envio = envios.Single(e => e.Id == parada.EnvioId);
                if (envio.Estado == EstadoEnvio.AsignadoRuta) envio.CambiarEstado(EstadoEnvio.EnDeposito, envio.Version);
                parada.Completar();
            }
            hoja.RegistrarFin(op.OcurridaEn); return null;
        }
        var actual = envios.SingleOrDefault(x => x.Id == op.EnvioId);
        var paradaActual = hoja.Paradas.SingleOrDefault(x => x.EnvioId == op.EnvioId);
        Exigir(actual is not null && paradaActual is not null, "El envío no pertenece a esta hoja.");
        Exigir(op.VersionEnvio == actual!.Version, "El envío cambió en el servidor; se conserva el hecho para resolución del despachador.");
        var propios = bultos.Where(x => x.EnvioId == actual.Id).ToArray();
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
            if (Completa(prueba, politica)) actual.CambiarEstado(EstadoEnvio.Entregado, actual.Version);
            else actual.RegistrarProgresoPrueba(actual.Version);
            return actual.Estado == EstadoEnvio.Entregado ? "Prueba completa; entrega validada automáticamente." : "La entrega conserva prueba pendiente.";
        }
        Exigir(paradaActual!.Activa && actual.Estado is (EstadoEnvio.EnTransito or EstadoEnvio.NoEntregado) && propios.All(x => x.Custodia == EstadoCustodiaBulto.Repartidor
            && x.AccesoCustodioId == hoja.RepartidorAccesoId && x.RutaCustodiaId == hoja.Id), "La entrega fue suspendida, cambió de estado o tiene otra custodia.");
        if (op.Tipo == "IntentoFallido")
        {
            Exigir(datos.Motivo is not null && politica.Motivos.Contains(datos.Motivo, StringComparer.Ordinal), "El motivo no pertenece a la política de esta hoja.");
            Exigir(!politica.RequiereFotoIntento || await Archivo(hoja, actual.Id, datos.FotoId, "Foto", ct), "El intento requiere una foto subida y verificada.");
            var anteriores = await db.OperacionesJornada.CountAsync(x => x.OperadorId == hoja.OperadorId && x.EnvioId == actual.Id && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada", ct);
            Exigir(anteriores < politica.MaximoIntentos, "Se alcanzó el máximo de intentos configurado.");
            if (actual.Estado == EstadoEnvio.NoEntregado) actual.CambiarEstado(EstadoEnvio.EnTransito, actual.Version);
            actual.CambiarEstado(EstadoEnvio.NoEntregado, actual.Version); return null;
        }
        if (op.Tipo == "EntregaFisica")
        {
            var prueba = await AplicarPrueba(hoja, actual.Id, datos, ct);
            if (actual.Estado == EstadoEnvio.NoEntregado) actual.CambiarEstado(EstadoEnvio.EnTransito, actual.Version);
            actual.DeclararEntregaFisica(op.OcurridaEn, Completa(prueba, politica), actual.Version);
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
        Exigir(prueba is null || prueba.RutaId == hoja.Id, "La prueba pertenece a otra ruta; requiere resolución del despachador.");
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
    private static bool Completa(PruebaEntrega p, ConfiguracionReparto c) => p.Latitud is not null && p.Longitud is not null
        && p.PrecisionMetros is not null && p.PosicionEnUtc is not null && (!c.RequiereNombre || !string.IsNullOrWhiteSpace(p.Nombre))
        && (!c.RequiereDocumento || !string.IsNullOrWhiteSpace(p.Documento)) && (!c.RequiereFoto || p.FotoId is not null) && (!c.RequiereFirma || p.FirmaId is not null);
}
