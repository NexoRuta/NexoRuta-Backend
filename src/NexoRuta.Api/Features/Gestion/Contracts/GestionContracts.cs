using System.Text.Json.Serialization;
using App = NexoRuta.Application.Gestion;
namespace NexoRuta.Api.Features.Gestion.Contracts;

public sealed record IndicadorResponse(string Corte, string Clave, string Unidad, int Cantidad, int Cumplidos,
    int Incumplidos, int SinCompromiso, int EnPlazo, int PruebaPendiente, int NoEvaluables,
    int Bultos, decimal PesoKg, decimal VolumenM3, int Entregados, int Pendientes, int Devueltos)
{
    public static IndicadorResponse Desde(App.Indicador value) => new(value.Corte,value.Clave,value.Unidad,value.Cantidad,value.Cumplidos,value.Incumplidos,value.SinCompromiso,value.EnPlazo,value.PruebaPendiente,value.NoEvaluables,value.Bultos,value.PesoKg,value.VolumenM3,value.Entregados,value.Pendientes,value.Devueltos);
}

public sealed record DetalleOperacionResponse(Guid EnvioId, Guid ComercioId, Guid? ZonaId, string Zona,
    bool EsRetiro, string Estado, DateTimeOffset AdmitidaEnUtc, DateTimeOffset? CompromisoHastaUtc,
    DateTimeOffset? CompletadaEnUtc, string Cumplimiento, int Bultos, decimal PesoKg, decimal VolumenM3)
{
    public static DetalleOperacionResponse Desde(App.DetalleOperacion value) => new(value.EnvioId,value.ComercioId,value.ZonaId,value.Zona,value.EsRetiro,value.Estado,value.AdmitidaEnUtc,value.CompromisoHastaUtc,value.CompletadaEnUtc,value.Cumplimiento,value.Bultos,value.PesoKg,value.VolumenM3);
}

public sealed record MotivoGestionResponse(string Tipo, string Motivo, string Unidad, int Cantidad)
{
    public static MotivoGestionResponse Desde(App.MotivoGestion value) => new(value.Tipo,value.Motivo,value.Unidad,value.Cantidad);
}

public sealed record DetalleBultoGestionResponse(Guid BultoId, Guid? RepartidorId, Guid? RutaId, DateTimeOffset? EntregadoEnUtc, string Resultado, bool? EnFranja)
{
    public static DetalleBultoGestionResponse Desde(App.DetalleBultoGestion value) => new(value.BultoId,value.RepartidorId,value.RutaId,value.EntregadoEnUtc,value.Resultado,value.EnFranja);
}

public sealed record DetalleGestionResponse(DetalleOperacionResponse Operacion, IReadOnlyList<DetalleBultoGestionResponse> Bultos)
{
    public static DetalleGestionResponse Desde(App.DetalleGestion value) => new(DetalleOperacionResponse.Desde(value.Operacion),value.Bultos.Select(DetalleBultoGestionResponse.Desde).ToArray());
}

public sealed record DetalleMotivoResponse(Guid OperacionId, Guid EnvioId, Guid RepartidorId, Guid RutaId,
    DateTimeOffset OcurridaEnUtc, string Tipo, string Motivo, int Bultos)
{
    public static DetalleMotivoResponse Desde(App.DetalleMotivo value) => new(value.OperacionId,value.EnvioId,value.RepartidorId,value.RutaId,value.OcurridaEnUtc,value.Tipo,value.Motivo,value.Bultos);
}

public sealed record ReporteGestionResponse(DateTimeOffset ConsultadaEnUtc, DateTimeOffset? ActualizadaEnUtc,
    int PendientesProyeccion, string EstadoProyeccion, IReadOnlyList<IndicadorResponse> Indicadores, IReadOnlyList<MotivoGestionResponse> Motivos,
    int Total, IReadOnlyList<DetalleOperacionResponse> Detalles, IReadOnlyList<DetalleMotivoResponse> DetallesMotivos, int TotalMotivos)
{
    public static ReporteGestionResponse Desde(App.ReporteGestion value) => new(value.ConsultadaEnUtc,value.ActualizadaEnUtc,value.PendientesProyeccion,value.EstadoProyeccion,value.Indicadores.Select(IndicadorResponse.Desde).ToArray(),value.Motivos.Select(MotivoGestionResponse.Desde).ToArray(),value.Total,value.Detalles.Select(DetalleOperacionResponse.Desde).ToArray(),value.DetallesMotivos.Select(DetalleMotivoResponse.Desde).ToArray(),value.TotalMotivos);
}

public sealed record MovimientoCuentaResponse(Guid Id, Guid EnvioId, DateTimeOffset FechaUtc, string Concepto,
    decimal ImporteUyu, decimal TotalPersistidoUyu, string Tratamiento, Guid? LiquidacionId)
{
    public static MovimientoCuentaResponse Desde(App.MovimientoCuenta value) => new(value.Id,value.EnvioId,value.FechaUtc,value.Concepto,value.ImporteUyu,value.TotalPersistidoUyu,value.Tratamiento,value.LiquidacionId);
}

public sealed record LiquidacionResumenResponse(Guid Id, Guid ComercioId, string Estado, DateTimeOffset DesdeUtc,
    DateTimeOffset HastaUtc, DateTimeOffset CorteUtc, decimal TotalUyu, Guid PreparadaPor,
    DateTimeOffset PreparadaEnUtc, Guid? ConfirmadaPor, DateTimeOffset? ConfirmadaEnUtc)
{
    public static LiquidacionResumenResponse Desde(App.LiquidacionResumen value) => new(value.Id,value.ComercioId,value.Estado,value.DesdeUtc,value.HastaUtc,value.CorteUtc,value.TotalUyu,value.PreparadaPor,value.PreparadaEnUtc,value.ConfirmadaPor,value.ConfirmadaEnUtc);
}

public sealed record LineaLiquidacionResponse(Guid MovimientoId, Guid EnvioId, DateTimeOffset FechaUtc,
    string Concepto, decimal ImporteUyu, string Fuente)
{
    public static LineaLiquidacionResponse Desde(App.LineaLiquidacion value) => new(value.MovimientoId,value.EnvioId,value.FechaUtc,value.Concepto,value.ImporteUyu,value.Fuente);
}

public sealed record LiquidacionDetalleResponse(LiquidacionResumenResponse Documento, IReadOnlyList<LineaLiquidacionResponse> Lineas, IReadOnlyList<TransicionLiquidacionResponse> Transiciones)
{
    public static LiquidacionDetalleResponse Desde(App.LiquidacionDetalle value) => new(LiquidacionResumenResponse.Desde(value.Documento),value.Lineas.Select(LineaLiquidacionResponse.Desde).ToArray(),value.Transiciones.Select(TransicionLiquidacionResponse.Desde).ToArray());
}

public sealed record CuentaGestionResponse(Guid OperadorId, Guid ComercioId, decimal SaldoOperativoUyu,
    decimal TotalPeriodoUyu, decimal PendienteLiquidarUyu, int Total, IReadOnlyList<MovimientoCuentaResponse> Movimientos,
    IReadOnlyList<LiquidacionResumenResponse> Liquidaciones)
{
    public static CuentaGestionResponse Desde(App.CuentaGestion value) => new(value.OperadorId,value.ComercioId,value.SaldoOperativoUyu,value.TotalPeriodoUyu,value.PendienteLiquidarUyu,value.Total,value.Movimientos.Select(MovimientoCuentaResponse.Desde).ToArray(),value.Liquidaciones.Select(LiquidacionResumenResponse.Desde).ToArray());
}

public sealed record CorreccionRegistradaResponse(Guid Id)
{
    public static CorreccionRegistradaResponse Desde(App.CorreccionRegistrada value) => new(value.Id);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CorregirCuentaRequest(Guid Id, Guid MovimientoOriginalId, decimal ImporteUyu, string Motivo)
{
    public App.CorregirCuenta ToApplication() => new(Id,MovimientoOriginalId,ImporteUyu,Motivo);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PrepararLiquidacionRequest(Guid Id, Guid ComercioId, DateTimeOffset DesdeUtc, DateTimeOffset HastaUtc)
{
    public App.PrepararLiquidacion ToApplication() => new(Id,ComercioId,DesdeUtc,HastaUtc);
}

public sealed record TransicionLiquidacionResponse(string Estado, Guid ActorId, DateTimeOffset FechaUtc)
{
    public static TransicionLiquidacionResponse Desde(App.TransicionLiquidacion value)=>new(value.Estado,value.ActorId,value.FechaUtc);
}

public sealed record OpcionGestionResponse(Guid Id, string Nombre);
public sealed record OpcionesGestionResponse(IReadOnlyList<OpcionGestionResponse> Opciones, bool HayMas)
{
    public static OpcionesGestionResponse Desde(App.OpcionesGestion value)
        => new(value.Opciones.Select(o=>new OpcionGestionResponse(o.Id,o.Nombre)).ToArray(),value.HayMas);
}
