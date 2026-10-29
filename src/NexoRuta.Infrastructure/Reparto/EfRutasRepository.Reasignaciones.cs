using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Notificaciones;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfRutasRepository
{
    public async Task<DecisionDevolucionEnvioDatos?> IniciarDevolucionAsync(ContextoUsuario actor, Guid envioId,
        IniciarDevolucionEnvioDatos datos, CancellationToken ct)
    {
        var operadorId = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        var motivo = datos.Motivo?.Trim();
        if (envioId == Guid.Empty || datos.VersionEnvio < 0 || datos.ComandoId == Guid.Empty
            || string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500)
            throw new ArgumentException("Indicá un motivo de hasta 500 caracteres y la versión y comando vigentes.");

        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct);
        await Bloquear(operadorId, ct);

        var decisionExistente = await db.DecisionesDevolucionEnvio.SingleOrDefaultAsync(x =>
            x.OperadorId == operadorId && x.ComandoId == datos.ComandoId, ct);
        if (decisionExistente is not null)
        {
            if (decisionExistente.EnvioId != envioId || decisionExistente.VersionAnterior != datos.VersionEnvio
                || decisionExistente.Motivo != motivo || decisionExistente.UsuarioId != actor.UsuarioId
                || decisionExistente.AccesoId != actor.AccesoId || decisionExistente.OrganizacionId != operadorId)
                throw new ConflictoRepartoException("El identificador del comando ya fue usado con otro contenido o contexto.");
            var actual = await db.Envios.SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == envioId, ct);
            if (actual is null) return null;
            var repetida = Resultado(decisionExistente, actual, true);
            await tx.CommitAsync(ct);
            return repetida;
        }

        var envioBloqueado = await db.Envios.FromSqlInterpolated(
                $"SELECT * FROM public.\"Envios\" WHERE \"OperadorId\"={operadorId} AND \"Id\"={envioId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (envioBloqueado is null) return null;
        await db.Entry(envioBloqueado).ReloadAsync(ct);
        var envio = envioBloqueado;
        if (envio.Version != datos.VersionEnvio || envio.Estado != EstadoEnvio.NoEntregado
            || envio.EntregaFisicaEnUtc is not null || envio.EntregaFisicaRecibidaEnUtc is not null
            || envio.PlazoPruebaVenceEnUtc is not null)
            throw new ConflictoRepartoException("El envío cambió o ya no admite iniciar una devolución desde su estado actual.");

        var reglas = await db.VersionesReglasOperativas.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OperadorId == operadorId && x.Id == envio.VersionReglasId, ct);
        if (reglas?.PoliticaDevolucionAlAgotarIntentos != PoliticaDevolucionAlAgotarIntentos.DecisionOperativa
            || reglas.MaxIntentosEntrega is not { } maximo
            || reglas.IntervaloEntreIntentosMinutos is not { } intervalo)
            throw new ConflictoRepartoException("La versión de reglas asociada al envío no habilita una decisión operativa de devolución.");

        var payloads = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == operadorId
            && x.EnvioId == envioId && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada")
            .Select(x => x.Payload).ToArrayAsync(ct);
        var resumen = ResumenIntentosEntrega.Calcular(payloads, intervalo);
        if (resumen.Cantidad < maximo)
            throw new ConflictoRepartoException("Todavía quedan intentos disponibles según las reglas asociadas al envío.");
        if (await db.Bultos.AnyAsync(x => x.OperadorId == operadorId && x.EnvioId == envioId
            && x.Custodia == EstadoCustodiaBulto.Destinatario, ct))
            throw new ConflictoRepartoException("El envío ya registra custodia de entrega y no admite iniciar una devolución por intentos.");

        var rutasConParadaActiva = await db.HojasRuta.Include(x => x.Paradas).Where(x => x.OperadorId == operadorId
            && x.Paradas.Any(p => p.EnvioId == envioId && p.Activa)).ToArrayAsync(ct);
        if (rutasConParadaActiva.Length > 1)
            throw new ConflictoRepartoException("El envío tiene más de una parada activa y requiere revisión operativa.");

        var ahora = reloj.GetUtcNow().ToUniversalTime();
        var decision = new DecisionDevolucionEnvio(operadorId, envioId, envio.VersionReglasId, datos.ComandoId,
            envio.Version, motivo, actor.UsuarioId, actor.AccesoId, operadorId, ahora);
        await db.SetShipmentHistoryContextAsync("DecisionDevolucionEnvio", decision.Id, datos.ComandoId,
            ahora, cancellationToken: ct);
        db.DecisionesDevolucionEnvio.Add(decision);
        envio.IniciarDevolucionPorDecision(envio.Version);

        var paradaActiva = rutasConParadaActiva.SingleOrDefault();
        if (paradaActiva is not null)
        {
            const string motivoSuspension = "Devolución iniciada por decisión operativa.";
            paradaActiva.SuspenderParada(envioId, motivoSuspension);
            if (paradaActiva.Estado == EstadoHojaRuta.EnCurso && paradaActiva.SalidaEnUtc is not null)
            {
                var aviso = new AvisoReparto(operadorId, paradaActiva.RepartidorAccesoId, paradaActiva.Id,
                    envioId, envio.Version, "DevolucionIniciada", motivo, ahora);
                db.AvisosReparto.Add(aviso);
                db.OutboxMessages.Add(OutboxMessage.ForShipment(operadorId, envioId,
                    OutboxMessageTypes.DeliveryChanged, OutboxDestino.PushFcm,
                    JsonSerializer.Serialize(new { avisoId = aviso.Id })));
            }
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictoRepartoException("El envío o la ruta cambiaron en otra operación; actualizá el estado antes de iniciar la devolución.");
        }
        var resultado = Resultado(decision, envio, false);
        await tx.CommitAsync(ct);
        return resultado;
    }

    private static DecisionDevolucionEnvioDatos Resultado(DecisionDevolucionEnvio decision, Envio actual, bool repetida) =>
        new(decision.EnvioId, decision.Id, decision.ComandoId, EstadoEnvio.EnDevolucion.ToString(),
            decision.VersionNueva, decision.Motivo, decision.DecididaEnUtc, actual.Estado.ToString(), actual.Version, repetida);

    public async Task<ReprogramacionEnvioDatos?> ReprogramarAsync(Guid operadorId, Guid envioId,
        SolicitarReprogramacionEnvioDatos datos, CancellationToken ct)
    {
        if (datos.VersionEnvio < 0) throw new ArgumentException("Indicá la versión actual del envío.");
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct);
        await Bloquear(operadorId, ct);
        var envio = await db.Envios.SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == envioId, ct);
        if (envio is null) return null;
        if (envio.Version != datos.VersionEnvio || envio.Estado != EstadoEnvio.NoEntregado || envio.EntregaFisicaEnUtc is not null)
            throw new ConflictoRepartoException("El envío cambió o ya no requiere reprogramación tras un intento fallido.");

        var reglas = await db.VersionesReglasOperativas.SingleAsync(x => x.OperadorId == operadorId
            && x.Id == envio.VersionReglasId && x.Estado == EstadoVersionReglasOperativas.Publicada, ct);
        if (reglas.MaxIntentosEntrega is not { } maximo || reglas.IntervaloEntreIntentosMinutos is not { } intervalo)
            throw new ConflictoRepartoException("La versión de reglas del envío no permite determinar los intentos restantes.");
        var payloads = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == operadorId
            && x.EnvioId == envioId && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada")
            .Select(x => x.Payload).ToArrayAsync(ct);
        var resumen = ResumenIntentosEntrega.Calcular(payloads, intervalo);
        if (resumen.Cantidad == 0)
            throw new ConflictoRepartoException("No se encontró un intento fallido aplicado para reprogramar este envío.");
        if (resumen.Cantidad >= maximo)
            throw new ConflictoRepartoException("No quedan intentos disponibles según las reglas asociadas al envío.");

        var hojasAnteriores = await db.HojasRuta.Include(x => x.Paradas).Where(x => x.OperadorId == operadorId
            && x.Paradas.Any(p => p.EnvioId == envioId && p.Activa)).ToArrayAsync(ct);
        var ahora = reloj.GetUtcNow().ToUniversalTime();
        var correlacion = Guid.CreateVersion7();
        await db.SetShipmentHistoryContextAsync("ReprogramacionEnvio", correlacion,
            occurredAt: ahora, cancellationToken: ct);
        envio.CambiarEstado(EstadoEnvio.Reprogramado, envio.Version);
        const string motivo = "Reprogramado tras un intento fallido.";
        foreach (var anterior in hojasAnteriores) anterior.SuspenderParada(envioId, motivo);

        var rutaEnCurso = hojasAnteriores.SingleOrDefault(x => x.Estado is EstadoHojaRuta.Despachada or EstadoHojaRuta.EnCurso);
        if (rutaEnCurso is not null)
        {
            var aviso = new AvisoReparto(operadorId, rutaEnCurso.RepartidorAccesoId, rutaEnCurso.Id, envioId,
                envio.Version, "Reasignacion", motivo, ahora);
            db.AvisosReparto.Add(aviso);
            db.OutboxMessages.Add(OutboxMessage.ForShipment(operadorId, envioId, OutboxMessageTypes.DeliveryChanged,
                OutboxDestino.PushFcm, JsonSerializer.Serialize(new { avisoId = aviso.Id })));
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictoRepartoException("El envío o la ruta cambiaron en otra operación; actualizá el estado antes de reprogramar.");
        }
        await tx.CommitAsync(ct);
        return new(envio.Id, envio.Version, resumen.Cantidad, maximo, resumen.ProximoIntentoElegibleUtc);
    }

    public async Task<HojaRutaDatos?> ObtenerPropiaAsync(Guid operadorId, Guid repartidorId, Guid rutaId, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        var hoja = await db.HojasRuta.AsNoTracking().Include(x => x.Paradas).SingleOrDefaultAsync(h =>
            h.OperadorId == operadorId && h.Id == rutaId && h.RepartidorAccesoId == repartidorId && h.Estado != EstadoHojaRuta.Planificada, ct);
        var salida = hoja is null ? null : await Datos(hoja, ct);
        await tx.CommitAsync(ct); return salida;
    }

    public async Task<HojaRutaDatos?> ReasignarAsync(Guid operadorId, Guid envioId, ReasignarEnvioDatos datos, CancellationToken ct)
    {
        if (datos.RutaDestinoId == Guid.Empty || datos.VersionEnvio < 0 || datos.VersionRutaDestino < 0
            || datos.LlegadaEstimadaUtc == default || string.IsNullOrWhiteSpace(datos.Motivo) || datos.Motivo.Length > 500)
            throw new ArgumentException("Indicá ruta futura, versiones, llegada estimada y motivo.");
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct);
        await Bloquear(operadorId, ct);
        var envio = await db.Envios.SingleOrDefaultAsync(e => e.OperadorId == operadorId && e.Id == envioId, ct);
        if (envio is null) return null;
        if (envio.EntregaFisicaEnUtc is not null || envio.Version != datos.VersionEnvio
            || envio.Estado is not (EstadoEnvio.AsignadoRuta or EstadoEnvio.Reprogramado))
            throw new ConflictoRepartoException("Solo se reasignan envíos asignados aún no despachados o reprogramados; un envío en tránsito no cambia a otra ruta.");
        var destino = await db.HojasRuta.Include(h => h.Paradas)
            .SingleOrDefaultAsync(h => h.Id == datos.RutaDestinoId && h.OperadorId == operadorId, ct);
        if (destino is null) return null;
        if (destino.Estado != EstadoHojaRuta.Planificada || destino.Version != datos.VersionRutaDestino
            || destino.Paradas.Any(p => p.EnvioId == envioId))
            throw new ConflictoRepartoException("La ruta destino cambió, ya fue despachada o contiene este envío.");
        var politica = await db.ConfiguracionesReparto.SingleAsync(p => p.OperadorId == operadorId && p.Version == destino.ConfiguracionVersion, ct);
        var zona = TimeZoneInfo.FindSystemTimeZoneById(politica.ZonaHoraria);
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.GetUtcNow(), zona).DateTime);
        if (destino.Fecha < hoy || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(datos.LlegadaEstimadaUtc, zona).DateTime) != destino.Fecha
            || destino.Paradas.Count > 0 && datos.LlegadaEstimadaUtc < destino.Paradas.MaxBy(p => p.Orden)!.LlegadaEstimadaUtc)
            throw new ConflictoRepartoException("La llegada debe corresponder a la fecha futura y al orden de la ruta destino.");
        if (destino.Paradas.Count(p => p.Activa) >= politica.MaximoParadas
            || !await RepartidorHabilitado(operadorId, destino.RepartidorAccesoId, ct)
            || !await db.Vehiculos.AnyAsync(v => v.OperadorId == operadorId && v.Id == destino.VehiculoId && v.Activo, ct))
            throw new ConflictoRepartoException("La ruta destino supera sus paradas o tiene repartidor/vehículo deshabilitados.");
        var ids = destino.Paradas.Where(p => p.Activa).Select(p => p.EnvioId).Append(envioId).ToArray();
        var bultos = await db.Bultos.Where(b => b.OperadorId == operadorId && ids.Contains(b.EnvioId)).ToArrayAsync(ct);
        if (!bultos.Any(b => b.EnvioId == envioId) || bultos.Sum(b => b.PesoGramos) > destino.VehiculoPesoMaximoGramos
            || bultos.Sum(b => b.LargoCentimetros * b.AnchoCentimetros * b.AltoCentimetros) > destino.VehiculoVolumenMaximoCm3)
            throw new ConflictoRepartoException("La reasignación supera el peso o volumen de la ruta destino.");
        var propias = bultos.Where(b => b.EnvioId == envioId).ToArray();
        if (propias.Any(b => b.Custodia != EstadoCustodiaBulto.Deposito))
            throw new ConflictoRepartoException("La reasignación requiere todos los bultos físicamente recibidos en depósito; no transfiere custodia entre repartidores.");
        var anteriores = await db.HojasRuta.Include(h => h.Paradas).Where(h => h.OperadorId == operadorId &&
            h.Paradas.Any(p => p.EnvioId == envioId && p.Activa)).ToArrayAsync(ct);
        var actual = anteriores.SingleOrDefault(h => h.Estado is EstadoHojaRuta.Despachada or EstadoHojaRuta.EnCurso);
        var correlacion = Guid.CreateVersion7();
        await db.SetShipmentHistoryContextAsync("ReasignacionRuta", correlacion, correlacion,
            reloj.GetUtcNow(), "CambioRuta", ct);
        foreach (var anterior in anteriores.Where(h => h.Paradas.Any(p => p.EnvioId == envioId && p.Activa)))
            anterior.SuspenderParada(envioId, datos.Motivo);
        // Libera el índice de parada activa antes de insertar la nueva, dentro de la misma transacción.
        await db.SaveChangesAsync(ct);
        destino.AgregarParada(envioId, datos.LlegadaEstimadaUtc);
        if (envio.Estado == EstadoEnvio.Reprogramado)
            envio.CambiarEstado(EstadoEnvio.AsignadoRuta, envio.Version);
        else
            envio.RegistrarReasignacionSinCambioDeEstado(envio.Version);
        if (actual is not null)
        {
            var aviso = new AvisoReparto(operadorId, actual.RepartidorAccesoId, actual.Id, envioId, envio.Version,
                "Reasignacion", datos.Motivo, reloj.GetUtcNow());
            db.AvisosReparto.Add(aviso);
            db.OutboxMessages.Add(OutboxMessage.ForShipment(operadorId, envioId, OutboxMessageTypes.DeliveryChanged,
                OutboxDestino.PushFcm, JsonSerializer.Serialize(new { avisoId = aviso.Id })));
        }
        await db.SaveChangesAsync(ct);
        var salida = await Datos(destino, ct);
        await tx.CommitAsync(ct); return salida;
    }
}
