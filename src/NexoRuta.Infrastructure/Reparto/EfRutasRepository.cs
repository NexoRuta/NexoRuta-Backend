using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfRutasRepository(NexoRutaDbContext db, TimeProvider reloj) : IRutasRepository
{
    public async Task<IReadOnlyList<HojaRutaDatos>> ListarAsync(Guid operadorId, Guid? repartidorId, DateOnly fecha, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct);
        var consulta = db.HojasRuta.AsNoTracking().Include(x => x.Paradas).Where(x => x.OperadorId == operadorId && x.Fecha == fecha);
        if (repartidorId is { } rep) consulta = consulta.Where(x => x.RepartidorAccesoId == rep
            && x.Estado != EstadoHojaRuta.Planificada && x.Estado != EstadoHojaRuta.Cancelada);
        var hojas = await consulta.OrderBy(x => x.Id).ToArrayAsync(ct);
        var salida = new List<HojaRutaDatos>();
        foreach (var hoja in hojas) salida.Add(await Datos(hoja, ct));
        await tx.CommitAsync(ct);
        return salida;
    }

    public async Task<HojaRutaDatos> PlanificarAsync(Guid operadorId, PlanificarRutaDatos datos, CancellationToken ct)
    {
        if (datos.Paradas is null || datos.Paradas.Count > 500 || datos.Paradas.Select(x => x.EnvioId).Distinct().Count() != datos.Paradas.Count)
            throw new ArgumentException("Seleccioná hasta 500 envíos distintos.");
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct); await Bloquear(operadorId, ct);
        var politica = await db.ConfiguracionesReparto.Where(x => x.OperadorId == operadorId).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct)
            ?? throw new ConflictoRepartoException("Configurá las reglas operativas antes de planificar.");
        if (datos.Paradas.Count > politica.MaximoParadas) throw new ConflictoRepartoException("La ruta supera el máximo de paradas configurado.");
        if (!await RepartidorHabilitado(operadorId, datos.RepartidorAccesoId, ct)) throw new ConflictoRepartoException("El repartidor no está habilitado para ejecutar rutas.");
        var vehiculo = await db.Vehiculos.SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == datos.VehiculoId && x.Activo, ct)
            ?? throw new ConflictoRepartoException("El vehículo no está activo en este operador.");
        var reservas = await db.HojasRuta.AsNoTracking()
            .Where(x => x.OperadorId == operadorId && x.Fecha == datos.Fecha
                && x.Estado != EstadoHojaRuta.Finalizada && x.Estado != EstadoHojaRuta.Cancelada
                && x.ReservaRecursosActiva
                && (x.VehiculoId == vehiculo.Id || x.RepartidorAccesoId == datos.RepartidorAccesoId))
            .OrderBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.Estado, x.Fecha, x.RepartidorAccesoId, x.VehiculoId, x.VehiculoMatricula,
                Paradas = x.Paradas.Count, ParadasActivas = x.Paradas.Count(p => p.Activa)
            }).ToArrayAsync(ct);
        if (reservas.Length > 0)
        {
            var detalles = reservas.Select(r =>
            {
                var recursos = new List<string>();
                if (r.RepartidorAccesoId == datos.RepartidorAccesoId) recursos.Add("el repartidor seleccionado");
                if (r.VehiculoId == vehiculo.Id) recursos.Add($"el vehículo {r.VehiculoMatricula}");
                var motivo = r.Estado == EstadoHojaRuta.Planificada && r.Paradas == 0
                    ? "Es una ruta vacía que conserva la reserva para recibir reasignaciones."
                    : $"Permanece {r.Estado} con {r.ParadasActivas} parada(s) activa(s).";
                return $"La ruta {r.Id} del {r.Fecha:yyyy-MM-dd} reserva {string.Join(" y ", recursos)}. {motivo}";
            });
            throw new ConflictoRepartoException($"No se puede planificar. {string.Join(" ", detalles)}");
        }
        var ids = datos.Paradas.Select(x => x.EnvioId).ToArray();
        var envios = await db.Envios.Where(x => x.OperadorId == operadorId && ids.Contains(x.Id)).ToArrayAsync(ct);
        if (envios.Length != ids.Length) throw new ConflictoRepartoException("Algún envío no pertenece al operador.");
        if (envios.Any(x => x.Estado is not (EstadoEnvio.EnDeposito or EstadoEnvio.Reprogramado)))
            throw new ConflictoRepartoException("Solo pueden planificarse envíos en depósito o reprogramados.");
        if (await db.ParadasRuta.AnyAsync(x => x.OperadorId == operadorId && ids.Contains(x.EnvioId) && x.Activa, ct))
            throw new ConflictoRepartoException("Algún envío ya pertenece a otra ruta activa.");
        var direcciones = envios.Select(x => x.DireccionId).ToArray();
        if (await db.Direcciones.CountAsync(x => x.OperadorId == operadorId && direcciones.Contains(x.Id)
                && x.ConfirmadaEnUtc != null && x.Latitud != null && x.Longitud != null, ct) != direcciones.Distinct().Count())
            throw new ConflictoRepartoException("El comercio debe confirmar todos los destinos antes de planificar.");
        var bultos = await db.Bultos.Where(x => x.OperadorId == operadorId && ids.Contains(x.EnvioId)).ToArrayAsync(ct);
        if (envios.Any(x => !bultos.Any(b => b.EnvioId == x.Id))) throw new ConflictoRepartoException("Todos los envíos deben tener bultos.");
        if (bultos.Any(x => x.Custodia != EstadoCustodiaBulto.Deposito))
            throw new ConflictoRepartoException("La planificación requiere que todos los bultos estén recibidos físicamente en depósito.");
        if (bultos.Sum(x => x.PesoGramos) > vehiculo.PesoMaximoGramos
            || bultos.Sum(x => x.LargoCentimetros * x.AnchoCentimetros * x.AltoCentimetros) > vehiculo.VolumenMaximoCm3)
            throw new ConflictoRepartoException("La ruta supera la capacidad de peso o volumen del vehículo.");
        var hoja = new HojaRuta(operadorId, datos.RepartidorAccesoId, vehiculo, datos.Fecha, politica.Version);
        var zona = TimeZoneInfo.FindSystemTimeZoneById(politica.ZonaHoraria);
        await db.SetShipmentHistoryContextAsync("PlanificacionRuta", hoja.Id, hoja.Id, reloj.GetUtcNow(), cancellationToken: ct);
        foreach (var parada in datos.Paradas)
        {
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(parada.LlegadaEstimadaUtc, zona).DateTime) != datos.Fecha)
                throw new ArgumentException("Las llegadas deben corresponder a la fecha de la ruta en la zona del operador.");
            hoja.AgregarParada(parada.EnvioId, parada.LlegadaEstimadaUtc);
            var envio = envios.Single(x => x.Id == parada.EnvioId);
            envio.CambiarEstado(EstadoEnvio.AsignadoRuta, envio.Version);
        }
        db.HojasRuta.Add(hoja); await db.SaveChangesAsync(ct);
        var salida = await Datos(hoja, ct); await tx.CommitAsync(ct); return salida;
    }

    public async Task<HojaRutaDatos?> DespacharAsync(Guid operadorId, Guid rutaId, long version, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct); await Bloquear(operadorId, ct);
        var hoja = await db.HojasRuta.Include(x => x.Paradas).SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == rutaId, ct);
        if (hoja is null) return null;
        if (hoja.Version != version || hoja.Estado != EstadoHojaRuta.Planificada) throw new ConflictoRepartoException("La ruta cambió o ya fue despachada.");
        if (!await RepartidorHabilitado(operadorId, hoja.RepartidorAccesoId, ct)
            || !await db.Vehiculos.AnyAsync(x => x.OperadorId == operadorId && x.Id == hoja.VehiculoId && x.Activo, ct))
            throw new ConflictoRepartoException("Repartidor o vehículo deshabilitados.");
        var ids = hoja.Paradas.Where(x => x.Activa).Select(x => x.EnvioId).ToArray();
        if (ids.Length == 0 || await db.Bultos.AnyAsync(x => x.OperadorId == operadorId && ids.Contains(x.EnvioId) && x.Custodia != EstadoCustodiaBulto.Deposito, ct)
            || await db.Envios.CountAsync(x => x.OperadorId == operadorId && ids.Contains(x.Id) && x.Estado == EstadoEnvio.AsignadoRuta, ct) != ids.Length)
            throw new ConflictoRepartoException("Todos los envíos deben seguir asignados y sus bultos recibidos físicamente en depósito.");
        hoja.Despachar(version); await db.SaveChangesAsync(ct);
        var salida = await Datos(hoja, ct); await tx.CommitAsync(ct); return salida;
    }

    public async Task<HojaRutaDatos?> CancelarAsync(Guid operadorId, Guid rutaId, long version, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct); await Bloquear(operadorId, ct);
        var hoja = await db.HojasRuta.Include(x => x.Paradas)
            .SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == rutaId, ct);
        if (hoja is null) return null;
        if (hoja.Version != version || hoja.Estado != EstadoHojaRuta.Planificada)
            throw new ConflictoRepartoException("La ruta cambió o ya no está planificada.");
        if (hoja.Paradas.Any(p => p.Activa))
            throw new ConflictoRepartoException("Primero cancelá o reasigná las paradas activas; no se cancelan envíos al cancelar una ruta.");
        hoja.Cancelar(version);
        await db.SaveChangesAsync(ct);
        var salida = await Datos(hoja, ct); await tx.CommitAsync(ct); return salida;
    }

    public async Task<bool> RecibirAsync(Guid operadorId, Guid envioId, long version, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct); await Bloquear(operadorId, ct);
        var envio = await db.Envios.SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == envioId, ct);
        if (envio is null) return false;
        if (envio.Version != version) throw new ConflictoRepartoException("El envío cambió antes de recibirlo.");
        var bultos = await db.Bultos.Where(x => x.OperadorId == operadorId && x.EnvioId == envioId).ToArrayAsync(ct);
        var pendientes = bultos.Where(b => b.Custodia == EstadoCustodiaBulto.Repartidor).ToArray();
        var cierres = await (from o in db.OperacionesJornada.AsNoTracking()
            join h in db.HojasRuta on o.RutaId equals h.Id
            where o.OperadorId == operadorId && h.OperadorId == operadorId && h.Estado == EstadoHojaRuta.Finalizada
                && o.Tipo == "FinJornada" && o.Resultado == "Aplicada"
                && pendientes.Select(b => b.RutaCustodiaId).Contains(o.RutaId)
            select new { o.RutaId, o.AccesoId, o.IncidenciasCierre }).ToArrayAsync(ct);
        bool TieneIncidencia(Bulto b) => cierres.Any(c => c.RutaId == b.RutaCustodiaId && c.AccesoId == b.AccesoCustodioId
            && LeerIncidencias(c.IncidenciasCierre).Any(i => i.BultoId == b.Id && i.EnvioId == envioId && i.VersionBulto == b.Version));
        if (bultos.Length == 0 || bultos.Any(x => x.Custodia is not (EstadoCustodiaBulto.SinRecepcion or EstadoCustodiaBulto.PendienteDeposito or EstadoCustodiaBulto.Deposito)
            && !(x.Custodia == EstadoCustodiaBulto.Repartidor && TieneIncidencia(x))))
            throw new ConflictoRepartoException("La recepción requiere bultos admitidos o declarados por el repartidor.");
        foreach (var bulto in bultos)
            if (bulto.Custodia == EstadoCustodiaBulto.Repartidor)
                bulto.RecibirTrasIncidenciaDeCierre(bulto.AccesoCustodioId!.Value, bulto.RutaCustodiaId!.Value);
            else bulto.RecibirEnDeposito();
        if (envio.Estado == EstadoEnvio.Admitido) envio.CambiarEstado(EstadoEnvio.EnDeposito, version);
        else if (envio.Estado == EstadoEnvio.EnDevolucion) envio.CambiarEstado(EstadoEnvio.Devuelto, version);
        else if (envio.Estado is not (EstadoEnvio.Reprogramado or EstadoEnvio.Cancelado or EstadoEnvio.AsignadoRuta
            or EstadoEnvio.EnDeposito or EstadoEnvio.NoEntregado or EstadoEnvio.EnTransito))
            throw new ConflictoRepartoException("El estado del envío no admite recepción.");
        if (envio.Estado is EstadoEnvio.Admitido or EstadoEnvio.EnDevolucion)
        {
            var correlacion = Guid.CreateVersion7();
            await db.SetShipmentHistoryContextAsync("RecepcionDeposito", correlacion, correlacion,
                reloj.GetUtcNow(), cancellationToken: ct);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }

    private Task<bool> RepartidorHabilitado(Guid op, Guid acceso, CancellationToken ct) =>
        (from r in db.Repartidores join a in db.AccesosUsuario on r.AccesoId equals a.Id
         join rol in db.Roles on a.RolId equals rol.Id
         where r.OperadorId == op && r.AccesoId == acceso && r.Activo && a.Activo
         && db.RolesPermisos.Any(rp => rp.RolId == rol.Id && db.Permisos.Any(p => p.Id == rp.PermisoId && p.Codigo == "reparto.ejecutar"))
         select r).AnyAsync(ct);

    private async Task<HojaRutaDatos> Datos(HojaRuta hoja, CancellationToken ct)
    {
        var politica = await db.ConfiguracionesReparto.AsNoTracking().SingleAsync(x => x.OperadorId == hoja.OperadorId && x.Version == hoja.ConfiguracionVersion, ct);
        var ids = hoja.Paradas.Select(x => x.EnvioId).ToArray();
        var envios = await (from e in db.Envios.AsNoTracking() join d in db.Direcciones on e.DireccionId equals d.Id
                           join persona in db.Destinatarios on e.DestinatarioId equals persona.Id
                           where e.OperadorId == hoja.OperadorId && ids.Contains(e.Id)
                           select new { Envio = e, Direccion = d, Destinatario = persona.Nombre }).ToArrayAsync(ct);
        var bultos = await db.Bultos.AsNoTracking().Where(x => x.OperadorId == hoja.OperadorId && ids.Contains(x.EnvioId)).ToArrayAsync(ct);
        var versiones = envios.Select(e => e.Envio.VersionReglasId).Distinct().ToArray();
        var reglas = await db.VersionesReglasOperativas.AsNoTracking().Include(v => v.RequisitosPrueba).Include(v => v.Motivos)
            .Where(v => v.OperadorId == hoja.OperadorId && versiones.Contains(v.Id) && v.Estado == EstadoVersionReglasOperativas.Publicada)
            .ToDictionaryAsync(v => v.Id, ct);
        var intentosAplicados = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == hoja.OperadorId
            && x.EnvioId != null && ids.Contains(x.EnvioId.Value)
            && x.Tipo == "IntentoFallido" && x.Resultado == "Aplicada")
            .Select(x => new { EnvioId = x.EnvioId!.Value, x.Payload }).ToArrayAsync(ct);
        var resumenesIntentos = envios.ToDictionary(x => x.Envio.Id, x =>
        {
            var version = reglas[x.Envio.VersionReglasId];
            return ResumenIntentosEntrega.Calcular(intentosAplicados.Where(a => a.EnvioId == x.Envio.Id)
                .Select(a => a.Payload), version.IntervaloEntreIntentosMinutos!.Value);
        });
        var paradas = hoja.Paradas.OrderBy(x => x.Orden).Select(p =>
        {
            var detalle = envios.Single(x => x.Envio.Id == p.EnvioId);
            return new ParadaRutaDatos(p.EnvioId, p.Orden, p.Activa, p.MotivoSuspension, p.LlegadaEstimadaUtc,
                detalle.Envio.Estado.ToString(), detalle.Envio.Version, detalle.Destinatario, detalle.Direccion.Descripcion,
                detalle.Direccion.Latitud ?? throw new InvalidOperationException("El destino no está confirmado."),
                detalle.Direccion.Longitud ?? throw new InvalidOperationException("El destino no está confirmado."),
                bultos.Where(b => b.EnvioId == p.EnvioId).Select(b => new BultoRutaDatos(b.Id, b.Codigo, b.PesoGramos,
                    b.LargoCentimetros * b.AnchoCentimetros * b.AltoCentimetros, b.Custodia.ToString(), b.Version)).ToArray(),
                ReglasDatos(reglas[detalle.Envio.VersionReglasId]), resumenesIntentos[p.EnvioId].Cantidad,
                resumenesIntentos[p.EnvioId].ProximoIntentoElegibleUtc);
        }).ToArray();
        var cierres = await db.OperacionesJornada.AsNoTracking().Where(o => o.OperadorId == hoja.OperadorId && o.RutaId == hoja.Id
            && o.Tipo == "FinJornada" && o.Resultado == "Aplicada").Select(o => o.IncidenciasCierre).ToArrayAsync(ct);
        return new(hoja.Id, hoja.JornadaId, hoja.RepartidorAccesoId, hoja.VehiculoId, hoja.VehiculoMatricula, hoja.Fecha,
            hoja.Estado.ToString(), hoja.Version, hoja.UltimaSecuencia,
            new(politica.Version, politica.ZonaHoraria, politica.MaximoParadas,
                politica.RequiereFotoIntento, politica.BloquearDiscrepanciasCarga), paradas, cierres.SelectMany(LeerIncidencias).ToArray(),
            hoja.ReservaRecursosActiva);
    }
    private static ReglasEnvioDatos ReglasDatos(VersionReglasOperativas v) => new(v.Id, v.NumeroVersionPublicada!.Value,
        v.MaxIntentosEntrega!.Value, v.IntervaloEntreIntentosMinutos!.Value, v.PoliticaDevolucionAlAgotarIntentos!.Value.ToString(),
        v.RequisitosPrueba.Any(p => p.Tipo == TipoPruebaEntrega.FirmaEnPantalla),
        v.RequisitosPrueba.Any(p => p.Tipo == TipoPruebaEntrega.Fotografia),
        v.RequisitosPrueba.Any(p => p.Tipo == TipoPruebaEntrega.NombreReceptor),
        v.RequisitosPrueba.Any(p => p.Tipo == TipoPruebaEntrega.DocumentoReceptor),
        v.Motivos.Where(m => m.Disponible).OrderBy(m => m.Codigo).Select(m => new MotivoNoEntregaDatos(m.Codigo, m.Descripcion)).ToArray());
    private static IncidenciaCierreDatos[] LeerIncidencias(string json) =>
        JsonSerializer.Deserialize<IncidenciaCierreDatos[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    private Task Contexto(Guid op, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('nexoruta.operador_id', {op.ToString()}, true)", ct);
    private Task Bloquear(Guid op, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Operadores\" WHERE \"Id\" = {op} FOR UPDATE", ct);
}
