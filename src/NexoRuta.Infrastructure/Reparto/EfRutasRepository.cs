using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Reparto;

public sealed partial class EfRutasRepository(NexoRutaDbContext db, TimeProvider reloj) : IRutasRepository
{
    public async Task<IReadOnlyList<HojaRutaDatos>> ListarAsync(Guid operadorId, Guid? repartidorId, DateOnly fecha, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct);
        var consulta = db.HojasRuta.AsNoTracking().Include(x => x.Paradas).Where(x => x.OperadorId == operadorId && x.Fecha == fecha);
        if (repartidorId is { } rep) consulta = consulta.Where(x => x.RepartidorAccesoId == rep && x.Estado != EstadoHojaRuta.Planificada);
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
        if (await db.HojasRuta.AnyAsync(x => x.OperadorId == operadorId && x.Fecha == datos.Fecha && x.Estado != EstadoHojaRuta.Finalizada
            && (x.VehiculoId == vehiculo.Id || x.RepartidorAccesoId == datos.RepartidorAccesoId), ct))
            throw new ConflictoRepartoException("El repartidor o el vehículo ya tienen una ruta pendiente para ese día.");
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
        if (bultos.Sum(x => x.PesoGramos) > vehiculo.PesoMaximoGramos
            || bultos.Sum(x => x.LargoCentimetros * x.AnchoCentimetros * x.AltoCentimetros) > vehiculo.VolumenMaximoCm3)
            throw new ConflictoRepartoException("La ruta supera la capacidad de peso o volumen del vehículo.");
        var hoja = new HojaRuta(operadorId, datos.RepartidorAccesoId, vehiculo, datos.Fecha, politica.Version);
        var zona = TimeZoneInfo.FindSystemTimeZoneById(politica.ZonaHoraria);
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

    public async Task<bool> RecibirAsync(Guid operadorId, Guid envioId, long version, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        await Contexto(operadorId, ct); await Bloquear(operadorId, ct);
        var envio = await db.Envios.SingleOrDefaultAsync(x => x.OperadorId == operadorId && x.Id == envioId, ct);
        if (envio is null) return false;
        if (envio.Version != version) throw new ConflictoRepartoException("El envío cambió antes de recibirlo.");
        var bultos = await db.Bultos.Where(x => x.OperadorId == operadorId && x.EnvioId == envioId).ToArrayAsync(ct);
        if (bultos.Length == 0 || bultos.Any(x => x.Custodia is not (EstadoCustodiaBulto.SinRecepcion or EstadoCustodiaBulto.PendienteDeposito or EstadoCustodiaBulto.Deposito)))
            throw new ConflictoRepartoException("La recepción requiere bultos admitidos o declarados por el repartidor.");
        foreach (var bulto in bultos) bulto.RecibirEnDeposito();
        if (envio.Estado == EstadoEnvio.Admitido) envio.CambiarEstado(EstadoEnvio.EnDeposito, version);
        else if (envio.Estado == EstadoEnvio.EnDevolucion) envio.CambiarEstado(EstadoEnvio.Devuelto, version);
        else if (envio.Estado is not (EstadoEnvio.Reprogramado or EstadoEnvio.Cancelado or EstadoEnvio.AsignadoRuta or EstadoEnvio.EnDeposito))
            throw new ConflictoRepartoException("El estado del envío no admite recepción.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }

    public async Task<bool> ConfirmarDestinoAsync(Guid comercioId, Guid usuarioId, Guid envioId, long version, double latitud, double longitud, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        var operadorId = await db.Envios.Where(x => x.Id == envioId && x.ComercioId == comercioId).Select(x => (Guid?)x.OperadorId).SingleOrDefaultAsync(ct);
        if (operadorId is null) return false;
        await Contexto(operadorId.Value, ct); await Bloquear(operadorId.Value, ct);
        var envio = await db.Envios.AsNoTracking().SingleAsync(x => x.Id == envioId && x.ComercioId == comercioId, ct);
        if (envio.Estado != EstadoEnvio.Admitido) throw new ConflictoRepartoException("El destino solo puede cambiar antes de la recepción en depósito.");
        var direccion = await db.Direcciones.SingleAsync(x => x.Id == envio.DireccionId && x.OperadorId == envio.OperadorId, ct);
        if (direccion.Version != version) throw new ConflictoRepartoException("El destino cambió. Recargalo antes de confirmar.");
        direccion.ConfirmarUbicacion(latitud, longitud, usuarioId, reloj.GetUtcNow(), version);
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
        var paradas = hoja.Paradas.OrderBy(x => x.Orden).Select(p =>
        {
            var detalle = envios.Single(x => x.Envio.Id == p.EnvioId);
            return new ParadaRutaDatos(p.EnvioId, p.Orden, p.Activa, p.MotivoSuspension, p.LlegadaEstimadaUtc,
                detalle.Envio.Estado.ToString(), detalle.Envio.Version, detalle.Destinatario, detalle.Direccion.Descripcion,
                detalle.Direccion.Latitud ?? throw new InvalidOperationException("El destino no está confirmado."),
                detalle.Direccion.Longitud ?? throw new InvalidOperationException("El destino no está confirmado."),
                bultos.Where(b => b.EnvioId == p.EnvioId).Select(b => new BultoRutaDatos(b.Id, b.Codigo, b.PesoGramos,
                    b.LargoCentimetros * b.AnchoCentimetros * b.AltoCentimetros, b.Custodia.ToString(), b.Version)).ToArray());
        }).ToArray();
        return new(hoja.Id, hoja.JornadaId, hoja.RepartidorAccesoId, hoja.VehiculoId, hoja.VehiculoMatricula, hoja.Fecha,
            hoja.Estado.ToString(), hoja.Version, hoja.UltimaSecuencia,
            new(politica.Version, politica.ZonaHoraria, politica.MaximoParadas, politica.MaximoIntentos, politica.RequiereFirma,
                politica.RequiereFoto, politica.RequiereNombre, politica.RequiereDocumento, politica.RequiereFotoIntento,
                politica.BloquearDiscrepanciasCarga, politica.Motivos.ToArray()), paradas);
    }
    private Task Contexto(Guid op, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('nexoruta.operador_id', {op.ToString()}, true)", ct);
    private Task Bloquear(Guid op, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Operadores\" WHERE \"Id\" = {op} FOR UPDATE", ct);
}
