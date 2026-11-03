namespace NexoRuta.Domain.Reparto;

public sealed record VentanaRuta(int DiaSemanaIso, TimeOnly Inicio, TimeOnly Fin)
{
    public bool Contiene(DateTimeOffset llegada, int servicioMinutos, TimeZoneInfo zona)
    {
        if (servicioMinutos < 0) return false;
        var local = TimeZoneInfo.ConvertTime(llegada, zona).DateTime;
        DateTime fin;
        try { fin = TimeZoneInfo.ConvertTime(llegada.AddMinutes(servicioMinutos), zona).DateTime; }
        catch (ArgumentOutOfRangeException) { return false; }
        foreach (var fecha in new[] { local.Date, local.Date.AddDays(-1) })
        {
            var dia = fecha.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)fecha.DayOfWeek;
            if (dia != DiaSemanaIso) continue;
            var desde = fecha.Add(Inicio.ToTimeSpan());
            var hasta = fecha.Add(Fin.ToTimeSpan()).AddDays(Fin < Inicio ? 1 : 0);
            if (local >= desde && local < hasta && fin <= hasta) return true;
        }
        return false;
    }
}
