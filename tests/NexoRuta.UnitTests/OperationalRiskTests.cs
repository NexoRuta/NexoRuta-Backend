using NexoRuta.Application.Operacion;
namespace NexoRuta.UnitTests;

public sealed class OperationalRiskTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(false, false, 60, 90, RiesgoOperativo.ProyeccionAtrasada)]
    [InlineData(false, true, 60, 90, RiesgoOperativo.ProyeccionAtrasada)]
    [InlineData(true, true, 60, 90, RiesgoOperativo.Finalizado)]
    [InlineData(true, false, null, 30, RiesgoOperativo.SinCompromiso)]
    [InlineData(true, false, 60, null, RiesgoOperativo.SinEstimacion)]
    [InlineData(true, false, 60, -1, RiesgoOperativo.SinEstimacion)]
    [InlineData(true, false, -1, null, RiesgoOperativo.PlazoIncumplido)]
    [InlineData(true, false, 0, 0, RiesgoOperativo.PlazoIncumplido)]
    [InlineData(true, false, 60, 90, RiesgoOperativo.EnRiesgo)]
    [InlineData(true, false, 60, 60, RiesgoOperativo.SinRiesgoDetectado)]
    public void RiskUsesTheExplicitDeadlineAndNeverInventsACommitment(bool fresh, bool final, int? deadline, int? eta, RiesgoOperativo expected)
        => Assert.Equal(expected, EvaluadorRiesgoOperativo.Evaluar(Now, fresh, final, deadline is { } d ? Now.AddMinutes(d) : null, eta is { } e ? Now.AddMinutes(e) : null));
}
