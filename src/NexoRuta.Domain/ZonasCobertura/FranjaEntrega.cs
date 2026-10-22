namespace NexoRuta.Domain.ZonasCobertura;

public sealed class FranjaEntrega
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid ZonaId { get; private set; }
    public DiaSemanaIso DiaSemanaIso { get; private set; }
    public TimeOnly HoraInicio { get; private set; }
    public TimeOnly HoraFin { get; private set; }
    public EstadoFranjaEntrega Estado { get; private set; }

    private FranjaEntrega() { }

    internal FranjaEntrega(
        Guid operadorId,
        Guid zonaId,
        DiaSemanaIso diaSemanaIso,
        TimeOnly horaInicio,
        TimeOnly horaFin,
        EstadoFranjaEntrega estado,
        Guid? id = null)
    {
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (zonaId == Guid.Empty) throw new ArgumentException("La zona es obligatoria.", nameof(zonaId));
        ValidarHorario(diaSemanaIso, horaInicio, horaFin, estado);
        if (id == Guid.Empty) throw new ArgumentException("El identificador de la franja no puede estar vacío.", nameof(id));

        Id = id ?? Guid.CreateVersion7();
        OperadorId = operadorId;
        ZonaId = zonaId;
        DiaSemanaIso = diaSemanaIso;
        HoraInicio = horaInicio;
        HoraFin = horaFin;
        Estado = estado;
    }

    internal bool EsActiva => Estado == EstadoFranjaEntrega.Activa;

    internal bool SeSolapa(FranjaEntrega otra)
    {
        var inicio = InicioSemanaEnTicks();
        var fin = FinSemanaEnTicks();
        var otroInicio = otra.InicioSemanaEnTicks();
        var otroFin = otra.FinSemanaEnTicks();
        const long ticksSemana = 7 * TimeSpan.TicksPerDay;

        for (var desplazamiento = -ticksSemana; desplazamiento <= ticksSemana; desplazamiento += ticksSemana)
            if (inicio < otroFin + desplazamiento && otroInicio + desplazamiento < fin)
                return true;

        return false;
    }

    internal bool Actualizar(DiaSemanaIso dia, TimeOnly inicio, TimeOnly fin, EstadoFranjaEntrega estado)
    {
        ValidarHorario(dia, inicio, fin, estado);
        if (DiaSemanaIso == dia && HoraInicio == inicio && HoraFin == fin && Estado == estado)
            return false;
        DiaSemanaIso = dia;
        HoraInicio = inicio;
        HoraFin = fin;
        Estado = estado;
        return true;
    }

    private long InicioSemanaEnTicks() => ((int)DiaSemanaIso - 1) * TimeSpan.TicksPerDay + HoraInicio.Ticks;

    private long FinSemanaEnTicks()
    {
        var duration = HoraFin > HoraInicio
            ? HoraFin.Ticks - HoraInicio.Ticks
            : TimeSpan.TicksPerDay - HoraInicio.Ticks + HoraFin.Ticks;
        return InicioSemanaEnTicks() + duration;
    }

    private static void ValidarHorario(DiaSemanaIso dia, TimeOnly inicio, TimeOnly fin, EstadoFranjaEntrega estado)
    {
        if (!Enum.IsDefined(dia)) throw new ArgumentOutOfRangeException(nameof(dia));
        if (!Enum.IsDefined(estado)) throw new ArgumentOutOfRangeException(nameof(estado));
        if (inicio == fin)
            throw new ArgumentException("La hora de inicio y fin no pueden ser iguales.", nameof(fin));
        if (inicio.Ticks % 10 != 0 || fin.Ticks % 10 != 0)
            throw new ArgumentException("Las horas deben tener precisión máxima de microsegundos, compatible con PostgreSQL.");
    }
}
