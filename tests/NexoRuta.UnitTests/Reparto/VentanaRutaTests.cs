using NexoRuta.Domain.Reparto;
namespace NexoRuta.UnitTests.Reparto;
public sealed class VentanaRutaTests
{
    [Theory]
    [InlineData("2026-11-02T12:00:00Z", 0, true)]
    [InlineData("2026-11-02T11:59:59Z", 0, false)]
    [InlineData("2026-11-02T20:30:00Z", 30, true)]
    [InlineData("2026-11-02T20:30:00Z", 31, false)]
    [InlineData("2026-11-02T21:00:00Z", 0, false)]
    [InlineData("2026-11-03T12:00:00Z", 0, false)]
    public void LocalDayAndWholeServiceMustFit(string arrival, int minutes, bool expected)
    {
        var window = new VentanaRuta(1, new(9,0), new(18,0));
        Assert.Equal(expected, window.Contiene(DateTimeOffset.Parse(arrival), minutes, TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")));
    }
    [Theory]
    [InlineData("2026-11-03T02:00:00Z", 60, true)]
    [InlineData("2026-11-03T03:00:00Z", 60, true)]
    [InlineData("2026-11-03T03:00:00Z", 61, false)]
    [InlineData("2026-11-03T04:00:00Z", 0, false)]
    public void OvernightWindowBelongsToOpeningDay(string arrival, int minutes, bool expected)
    {
        Assert.Equal(expected, new VentanaRuta(1,new(22,0),new(1,0)).Contiene(DateTimeOffset.Parse(arrival),minutes,TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")));
    }
}
