using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Exceptions;
namespace NexoRuta.Application.Gestion;

public sealed record FiltroGestion(Guid OperadorId, DateTimeOffset Desde, DateTimeOffset Hasta,
    Guid? ComercioId = null, Guid? ZonaId = null, Guid? RepartidorId = null, int Pagina = 1, int Tamano = 25)
{
    public void Validar()
    {
        if (OperadorId == Guid.Empty || Desde == default || Hasta <= Desde || Hasta - Desde > TimeSpan.FromDays(366)
            || Pagina is < 1 or > 10000 || Tamano is < 1 or > 100 || ComercioId == Guid.Empty || ZonaId == Guid.Empty || RepartidorId == Guid.Empty)
            throw new ArgumentException("Indicá operador, período de hasta 366 días y página válida.");
    }
}
public sealed record Indicador(string Corte, string Clave, string Unidad, int Cantidad, int Cumplidos,
    int Incumplidos, int SinCompromiso, int EnPlazo, int PruebaPendiente, int NoEvaluables,
    int Bultos, decimal PesoKg, decimal VolumenM3, int Entregados, int Pendientes, int Devueltos);
public sealed record DetalleOperacion(Guid EnvioId, Guid ComercioId, Guid? ZonaId, string Zona,
    bool EsRetiro, string Estado, DateTimeOffset AdmitidaEnUtc, DateTimeOffset? CompromisoHastaUtc,
    DateTimeOffset? CompletadaEnUtc, string Cumplimiento, int Bultos, decimal PesoKg, decimal VolumenM3);
public sealed record MotivoGestion(string Tipo, string Motivo, string Unidad, int Cantidad);
public sealed record DetalleBultoGestion(Guid BultoId, Guid? RepartidorId, Guid? RutaId, DateTimeOffset? EntregadoEnUtc, string Resultado, bool? EnFranja);
public sealed record DetalleGestion(DetalleOperacion Operacion, IReadOnlyList<DetalleBultoGestion> Bultos);
public sealed record DetalleMotivo(Guid OperacionId, Guid EnvioId, Guid RepartidorId, Guid RutaId,
    DateTimeOffset OcurridaEnUtc, string Tipo, string Motivo, int Bultos);
public sealed record ReporteGestion(DateTimeOffset ConsultadaEnUtc, DateTimeOffset? ActualizadaEnUtc,
    int PendientesProyeccion, string EstadoProyeccion, IReadOnlyList<Indicador> Indicadores, IReadOnlyList<MotivoGestion> Motivos,
    int Total, IReadOnlyList<DetalleOperacion> Detalles, IReadOnlyList<DetalleMotivo> DetallesMotivos, int TotalMotivos);
public sealed record MovimientoCuenta(Guid Id, Guid EnvioId, DateTimeOffset FechaUtc, string Concepto,
    decimal ImporteUyu, decimal TotalPersistidoUyu, string Tratamiento, Guid? LiquidacionId);
public sealed record LiquidacionResumen(Guid Id, Guid ComercioId, string Estado, DateTimeOffset DesdeUtc,
    DateTimeOffset HastaUtc, DateTimeOffset CorteUtc, decimal TotalUyu, Guid PreparadaPor,
    DateTimeOffset PreparadaEnUtc, Guid? ConfirmadaPor, DateTimeOffset? ConfirmadaEnUtc);
public sealed record LineaLiquidacion(Guid MovimientoId, Guid EnvioId, DateTimeOffset FechaUtc,
    string Concepto, decimal ImporteUyu, string Fuente);
public sealed record TransicionLiquidacion(string Estado, Guid ActorId, DateTimeOffset FechaUtc);
public sealed record LiquidacionDetalle(LiquidacionResumen Documento, IReadOnlyList<LineaLiquidacion> Lineas, IReadOnlyList<TransicionLiquidacion> Transiciones);
public sealed record CuentaGestion(Guid OperadorId, Guid ComercioId, decimal SaldoOperativoUyu,
    decimal TotalPeriodoUyu, decimal PendienteLiquidarUyu, int Total, IReadOnlyList<MovimientoCuenta> Movimientos,
    IReadOnlyList<LiquidacionResumen> Liquidaciones);
public sealed record CorreccionRegistrada(Guid Id);
public sealed record CorregirCuenta(Guid Id, Guid MovimientoOriginalId, decimal ImporteUyu, string Motivo);
public sealed record PrepararLiquidacion(Guid Id, Guid ComercioId, DateTimeOffset DesdeUtc, DateTimeOffset HastaUtc);

public sealed record OpcionGestion(Guid Id, string Nombre);
public sealed record OpcionesGestion(IReadOnlyList<OpcionGestion> Opciones, bool HayMas);

public interface IGestionRepository
{
    Task<OpcionesGestion> OpcionesAsync(Guid operador, string tipo, string buscar, int pagina, Guid[] seleccionado, CancellationToken ct);
    Task<ReporteGestion> ReporteAsync(FiltroGestion filtro, CancellationToken ct);
    Task<DetalleGestion?> DetalleAsync(Guid operador, Guid envio, CancellationToken ct);
    Task<Guid> CorregirAsync(CorregirCuenta command, CancellationToken ct);
    Task<CuentaGestion> CuentaAsync(FiltroGestion filtro, Guid comercio, CancellationToken ct);
    Task<LiquidacionDetalle?> LiquidacionAsync(Guid operador, Guid id, CancellationToken ct);
    Task<LiquidacionDetalle> PrepararAsync(Guid operador, Guid actor, PrepararLiquidacion command, CancellationToken ct);
    Task<LiquidacionDetalle?> TransicionAsync(Guid operador, Guid actor, Guid id, bool confirmar, CancellationToken ct);
}
public sealed class GestionUseCases(IUsuarioActual usuario, IGestionRepository repository)
{
    private async Task<Guid?> Ambito(Guid operador, bool gestion, CancellationToken ct)
    {
        if(operador==Guid.Empty)throw new ArgumentException("Seleccioná un operador válido.");
        var actor = await usuario.ObtenerAsync(ct);
        if (actor.ComercioId is { } comercio)
        {
            if (gestion) throw new AccesoNoPermitidoException();
            return comercio;
        }
        if (actor.OperadorId != operador) throw new AccesoNoPermitidoException();
        return null;
    }
    public async Task<OpcionesGestion> OpcionesAsync(Guid operador, string tipo, string? buscar, int pagina, Guid[] seleccionado, CancellationToken ct)
    {
        await Ambito(operador, tipo is not ("Envio" or "Bulto"), ct);
        if (tipo is not ("Comercio" or "Zona" or "Repartidor" or "Envio" or "Bulto" or "Ruta") || pagina is < 1 or > 10000 || buscar?.Length > 160 || seleccionado.Length > 100)
            throw new ArgumentException("Revisá la búsqueda y la página.");
        return await repository.OpcionesAsync(operador, tipo, buscar?.Trim() ?? "", pagina, seleccionado, ct);
    }
    public async Task<ReporteGestion> ReporteAsync(FiltroGestion f, CancellationToken ct)
    {
        f.Validar(); await Ambito(f.OperadorId, true, ct);
        return await repository.ReporteAsync(f, ct);
    }
    public async Task<DetalleGestion?> DetalleAsync(Guid operador, Guid envio, CancellationToken ct)
    {
        await Ambito(operador, true, ct); return await repository.DetalleAsync(operador, envio, ct);
    }
    public async Task<CuentaGestion> CuentaAsync(FiltroGestion f, CancellationToken ct)
    {
        f.Validar(); var comercio = await Ambito(f.OperadorId, false, ct) ?? f.ComercioId
            ?? throw new ArgumentException("Seleccioná el comercio.");
        // Commerce identity always comes from the validated session, even if a query ID is supplied.
        if (f.ComercioId is { } requested && requested != comercio) throw new AccesoNoPermitidoException();
        return await repository.CuentaAsync(f, comercio, ct);
    }
    public async Task<LiquidacionDetalle?> LiquidacionAsync(Guid operador, Guid id, CancellationToken ct)
    {
        await Ambito(operador, false, ct); return await repository.LiquidacionAsync(operador, id, ct);
    }
    public async Task<LiquidacionDetalle> PrepararAsync(Guid operador, PrepararLiquidacion c, CancellationToken ct)
    {
        new FiltroGestion(operador, c.DesdeUtc, c.HastaUtc, c.ComercioId).Validar();
        if (c.Id == Guid.Empty) throw new ArgumentException("La solicitud requiere identidad idempotente.");
        await Ambito(operador, true, ct); var actor = await usuario.ObtenerAsync(ct);
        return await repository.PrepararAsync(operador, actor.UsuarioId, c, ct);
    }
    public async Task<Guid> CorregirAsync(Guid operador, CorregirCuenta c, CancellationToken ct)
    {
        await Ambito(operador, true, ct);
        NexoRuta.Domain.Gestion.ReglasGestion.ValidarCorreccion(c.Id,c.MovimientoOriginalId,c.ImporteUyu,c.Motivo);
        return await repository.CorregirAsync(c, ct);
    }
    public async Task<LiquidacionDetalle?> TransicionAsync(Guid operador, Guid id, bool confirmar, CancellationToken ct)
    {
        await Ambito(operador, true, ct); var actor = await usuario.ObtenerAsync(ct);
        var documento=await repository.LiquidacionAsync(operador,id,ct);
        if(documento is null)return null;
        NexoRuta.Domain.Gestion.ReglasGestion.Transicion(documento.Documento.Estado,confirmar);
        return await repository.TransicionAsync(operador, actor.UsuarioId, id, confirmar, ct);
    }
}
