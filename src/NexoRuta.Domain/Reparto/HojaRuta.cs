namespace NexoRuta.Domain.Reparto;

public sealed class HojaRuta
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid RepartidorAccesoId { get; private set; }
    public Guid VehiculoId { get; private set; }
    public string VehiculoMatricula { get; private set; } = "";
    public decimal VehiculoPesoMaximoGramos { get; private set; }
    public decimal VehiculoVolumenMaximoCm3 { get; private set; }
    public Guid JornadaId { get; private set; }
    public DateOnly Fecha { get; private set; }
    public long ConfiguracionVersion { get; private set; }
    public long Version { get; private set; }
    public EstadoHojaRuta Estado { get; private set; }
    public DateTimeOffset? InicioEnUtc { get; private set; }
    public DateTimeOffset? FinEnUtc { get; private set; }
    public long UltimaSecuencia { get; private set; }
    public DateTimeOffset? SalidaEnUtc { get; private set; }
    private readonly List<ParadaRuta> paradas = [];
    public IReadOnlyList<ParadaRuta> Paradas => paradas.AsReadOnly();
    private HojaRuta() { }

    public HojaRuta(Guid operadorId, Guid repartidorAccesoId, Vehiculo vehiculo, DateOnly fecha, long configuracionVersion)
    {
        if (operadorId == Guid.Empty || repartidorAccesoId == Guid.Empty || vehiculo.OperadorId != operadorId || !vehiculo.Activo
            || fecha == default || configuracionVersion < 1) throw new ArgumentException("La hoja requiere operador, repartidor, vehículo, fecha y política.");
        Id = Guid.CreateVersion7(); JornadaId = Guid.CreateVersion7(); OperadorId = operadorId;
        RepartidorAccesoId = repartidorAccesoId; VehiculoId = vehiculo.Id; Fecha = fecha; ConfiguracionVersion = configuracionVersion;
        VehiculoMatricula = vehiculo.Matricula;
        VehiculoPesoMaximoGramos = vehiculo.PesoMaximoGramos;
        VehiculoVolumenMaximoCm3 = vehiculo.VolumenMaximoCm3;
    }

    public void AgregarParada(Guid envioId, DateTimeOffset llegada)
    {
        if (Estado != EstadoHojaRuta.Planificada || paradas.Any(x => x.EnvioId == envioId))
            throw new InvalidOperationException("La ruta ya está despachada o el envío está repetido.");
        if (paradas.Count > 0 && llegada < paradas[^1].LlegadaEstimadaUtc)
            throw new ArgumentException("Las llegadas estimadas deben respetar el orden de las paradas.");
        paradas.Add(new ParadaRuta(Id, OperadorId, envioId, paradas.Count + 1, llegada));
        Version++;
    }

    public void Despachar(long versionEsperada)
    {
        if (Version != versionEsperada || Estado != EstadoHojaRuta.Planificada || paradas.Count == 0)
            throw new InvalidOperationException("La ruta cambió o no está lista para despachar.");
        Estado = EstadoHojaRuta.Despachada; Version++;
    }

    public void RegistrarInicio(DateTimeOffset instante)
    {
        if (Estado != EstadoHojaRuta.Despachada || instante == default) throw new InvalidOperationException("La ruta no está lista para iniciar.");
        Estado = EstadoHojaRuta.EnCurso; InicioEnUtc = instante.ToUniversalTime(); Version++;
    }

    public void ConfirmarSalida(DateTimeOffset instante)
    {
        if (Estado != EstadoHojaRuta.EnCurso || SalidaEnUtc is not null || instante == default)
            throw new InvalidOperationException("La salida requiere una jornada iniciada y carga sin confirmar.");
        SalidaEnUtc = instante.ToUniversalTime(); Version++;
    }

    public void RegistrarFin(DateTimeOffset instante)
    {
        if (Estado != EstadoHojaRuta.EnCurso || instante == default) throw new InvalidOperationException("No hay una jornada en curso para finalizar.");
        Estado = EstadoHojaRuta.Finalizada; FinEnUtc = instante.ToUniversalTime(); Version++;
    }

    public void ConfirmarSecuencia(long secuencia)
    {
        if (secuencia != UltimaSecuencia + 1) throw new InvalidOperationException("Las operaciones de la jornada deben conservar su orden.");
        UltimaSecuencia = secuencia; Version++;
    }

    public void SuspenderParada(Guid envioId, string motivo)
    {
        var parada = paradas.SingleOrDefault(x => x.EnvioId == envioId && x.Activa)
            ?? throw new InvalidOperationException("El envío ya no pertenece a las entregas activas de la ruta.");
        parada.Suspender(motivo); Version++;
    }
}
