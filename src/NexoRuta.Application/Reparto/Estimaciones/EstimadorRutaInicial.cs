using System.Security.Cryptography;
using System.Text.Json;

namespace NexoRuta.Application.Reparto.Estimaciones;

public sealed class EstimadorRutaInicial(IProveedorTramosViales? proveedor, TimeProvider reloj)
{
    public Task<EstimacionInicial> CalcularAsync(SolicitudEstimacionInicial solicitud, CancellationToken ct)
        => Calcular(solicitud, null, ct);

    public Task<EstimacionInicial> CalcularRecorridoAsync(SolicitudEstimacionInicial solicitud, PuntoVial origen, CancellationToken ct)
        => Calcular(solicitud, origen, ct);

    private async Task<EstimacionInicial> Calcular(SolicitudEstimacionInicial solicitud, PuntoVial? origenRecorrido, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var intentado = reloj.GetUtcNow();
        var huella = "";
        EstimacionInicial Fallo(EstadoEstimacionInicial estado) =>
            new(estado, huella, intentado, null, solicitud.ZonaHoraria, [], [], null, null, null);
        if (!DatosValidos(solicitud) || origenRecorrido is not null && (origenRecorrido.EsValido != true || !DatosBaseValidos(solicitud))) return Fallo(EstadoEstimacionInicial.DatosInsuficientes);
        if (origenRecorrido is null && solicitud.Paradas.All(p => !p.Activa)) return Fallo(EstadoEstimacionInicial.SinParadas);
        // Freeze caller-owned collections before the first await; never mutate the B3 source or manual schedule.
        solicitud = Normalizar(solicitud);
        huella = Huella(solicitud);
        var paradas = solicitud.Paradas.Where(p => p.Activa).ToArray();
        if (proveedor is null) return Fallo(EstadoEstimacionInicial.ProveedorNoConfigurado);

        var horarios = new List<HorarioCalculado>();
        var tramos = new List<TramoCalculado>();
        var origen = origenRecorrido ?? solicitud.Base!;
        var instante = solicitud.SalidaPlanificadaEnUtc!.Value;
        DateTimeOffset? vigencia = null;
        var vigenciaConocida = true;
        double distancia = 0;
        for (var i = 0; i <= paradas.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var destino = i < paradas.Length ? paradas[i].Punto! : solicitud.Base!;
            var pedido = new SolicitudTramoVial(huella, i, origen, destino, instante, solicitud.PerfilVial!);
            RespuestaTramoVial respuesta;
            try { respuesta = await proveedor.CalcularAsync(pedido, ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fallo(EstadoEstimacionInicial.TiempoAgotado); }
            catch (HttpRequestException) { return Fallo(EstadoEstimacionInicial.ProveedorNoDisponible); }
            ct.ThrowIfCancellationRequested();
            if (respuesta is null || respuesta.Solicitud != pedido || !Enum.IsDefined(respuesta.Estado))
                return Fallo(EstadoEstimacionInicial.RespuestaInvalida);
            if (respuesta.Estado != EstadoTramoVial.Encontrado) return Fallo(respuesta.Estado switch
            {
                EstadoTramoVial.NoConfigurado => EstadoEstimacionInicial.ProveedorNoConfigurado,
                EstadoTramoVial.RutaNoEncontrada => EstadoEstimacionInicial.RutaNoEncontrada,
                EstadoTramoVial.TiempoAgotado => EstadoEstimacionInicial.TiempoAgotado,
                EstadoTramoVial.CuotaExcedida => EstadoEstimacionInicial.CuotaExcedida,
                EstadoTramoVial.ProveedorNoDisponible => EstadoEstimacionInicial.ProveedorNoDisponible,
                _ => EstadoEstimacionInicial.RespuestaInvalida
            });
            var meta = respuesta.Procedencia;
            if (!RespuestaValida(respuesta, reloj.GetUtcNow()) || meta is null)
                return Fallo(EstadoEstimacionInicial.RespuestaInvalida);
            if (tramos.FirstOrDefault()?.Procedencia is { } primera &&
                (primera.Proveedor != meta.Proveedor || primera.Modelo != meta.Modelo || primera.Fuente != meta.Fuente || primera.Trafico != meta.Trafico))
                return Fallo(EstadoEstimacionInicial.RespuestaInvalida);
            distancia += respuesta.DistanciaMetros!.Value;
            if (!double.IsFinite(distancia)) return Fallo(EstadoEstimacionInicial.RespuestaInvalida);
            vigenciaConocida &= meta.ValidoHastaUtc is not null;
            if (meta.ValidoHastaUtc is { } hasta && (vigencia is null || hasta < vigencia)) vigencia = hasta;
            try
            {
                instante = instante.AddSeconds(respuesta.DuracionSegundos!.Value);
                if (i < paradas.Length)
                {
                    var llegada = instante;
                    instante = instante.AddMinutes(paradas[i].ServicioMinutos!.Value);
                    horarios.Add(new(paradas[i].EnvioId, paradas[i].LlegadaManualEnUtc, llegada, instante));
                }
            }
            catch (ArgumentOutOfRangeException) { return Fallo(EstadoEstimacionInicial.RespuestaInvalida); }
            tramos.Add(new(pedido, respuesta.DuracionSegundos!.Value, respuesta.DistanciaMetros!.Value, meta));
            origen = destino;
        }
        DateTimeOffset listo;
        try { listo = instante.AddMinutes(solicitud.MargenOperativoMinutos!.Value); }
        catch (ArgumentOutOfRangeException) { return Fallo(EstadoEstimacionInicial.RespuestaInvalida); }
        return new(EstadoEstimacionInicial.Calculada, huella, intentado, reloj.GetUtcNow(), solicitud.ZonaHoraria,
            horarios.AsReadOnly(), tramos.AsReadOnly(), instante, listo, vigenciaConocida ? vigencia : null);
    }

    // Read-only comparison: a stale calculation is never applied to a newer route or another operator.
    public static VigenciaEstimacion ObtenerVigencia(EstimacionInicial resultado, SolicitudEstimacionInicial actual, DateTimeOffset ahora)
    {
        if (resultado.Estado != EstadoEstimacionInicial.Calculada) return VigenciaEstimacion.Desconocida;
        if (!DatosValidos(actual) || actual.Paradas.All(p => !p.Activa) || resultado.Huella != Huella(Normalizar(actual))) return VigenciaEstimacion.Invalidada;
        if (ahora < resultado.CalculadoEnUtc || resultado.ValidoHastaUtc is null) return VigenciaEstimacion.Desconocida;
        return ahora >= resultado.ValidoHastaUtc ? VigenciaEstimacion.Vencida : VigenciaEstimacion.Vigente;
    }

    private static SolicitudEstimacionInicial Normalizar(SolicitudEstimacionInicial s) => s with
    {
        SalidaPlanificadaEnUtc = s.SalidaPlanificadaEnUtc?.ToUniversalTime(),
        Paradas = Array.AsReadOnly(s.Paradas.Where(p => p.Activa).OrderBy(p => p.Orden)
            .Select(p => p with { LlegadaManualEnUtc = p.LlegadaManualEnUtc.ToUniversalTime() }).ToArray())
    };
    private static string Huella(SolicitudEstimacionInicial s) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(s)));
    private static bool DatosValidos(SolicitudEstimacionInicial s)
    {
        if (s.OperadorId == Guid.Empty || s.RutaId == Guid.Empty || s.VersionRuta < 0 || s.Paradas is null || s.Paradas.Count > 500 || s.Paradas.Any(p => p is null))
            return false;
        var activas = s.Paradas.Where(p => p.Activa).ToArray();
        // A reservation without stops is not a zero-minute circuit, even if the base has not been configured.
        if (activas.Length == 0) return true;
        if (!DatosBaseValidos(s)
            || activas.Any(p => p.EnvioId == Guid.Empty || p.Orden < 1 || !p.PuntoConfirmado || p.Punto?.EsValido != true || p.ServicioMinutos is null or < 0 || p.LlegadaManualEnUtc == default)
            || activas.Select(p => p.EnvioId).Distinct().Count() != activas.Length || activas.Select(p => p.Orden).Distinct().Count() != activas.Length)
            return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(s.ZonaHoraria); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
        return true;
    }
    private static bool DatosBaseValidos(SolicitudEstimacionInicial s) => s.VersionBase is >= 1 && s.BaseConfirmada
        && s.Base?.EsValido == true && s.SalidaPlanificadaEnUtc is not null && s.SalidaPlanificadaEnUtc != default(DateTimeOffset)
        && s.MargenOperativoMinutos is >= 0 && !string.IsNullOrWhiteSpace(s.PerfilVial) && ZonaValida(s.ZonaHoraria);
    private static bool ZonaValida(string zona)
    {try{_=TimeZoneInfo.FindSystemTimeZoneById(zona);return true;}catch(Exception e)when(e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException){return false;}}
    private static bool RespuestaValida(RespuestaTramoVial r, DateTimeOffset ahora) =>
        r.DuracionSegundos is { } segundos && double.IsFinite(segundos) && segundos >= 0
        && r.DistanciaMetros is { } metros && double.IsFinite(metros) && metros >= 0
        && r.Procedencia is { } m && !string.IsNullOrWhiteSpace(m.Proveedor) && !string.IsNullOrWhiteSpace(m.Modelo) && !string.IsNullOrWhiteSpace(m.Atribucion)
        && Enum.IsDefined(m.Fuente) && Enum.IsDefined(m.Trafico) && m.CalculadoEnUtc != default && m.CalculadoEnUtc <= ahora
        && (m.ValidoHastaUtc is null || m.ValidoHastaUtc >= m.CalculadoEnUtc);
}
