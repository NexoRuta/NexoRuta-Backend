using NexoRuta.Application.Reparto.Estimaciones;
namespace NexoRuta.UnitTests.Reparto;
public sealed class PosicionProyeccionTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-01T23:59:30-03:00");
    private static PosicionProyeccion P(int seconds=10,double precision=20)=>new(Guid.NewGuid(),new(-34.9,-56.2),Now.AddSeconds(-seconds),Now,precision);
    [Fact] public void RecentAccurateAndMeasuredOrderAreDeterministic()
    {var recent=P();var old=P(100);Assert.Equal(recent,SelectorPosicion.Elegir(new[]{old,recent},Now,Now.AddHours(-1),new()).Posicion);}
    [Theory][InlineData(121,20,"GpsAntiguo")][InlineData(10,101,"GpsImpreciso")][InlineData(-1,20,"GpsFuturo")][InlineData(10,0,"GpsInvalido")]
    public void InvalidGpsNeverBecomesRecentFromReceipt(int seconds,double precision,string cause)
    {Assert.Equal(cause,SelectorPosicion.Elegir(new[]{P(seconds,precision)},Now,Now.AddHours(-1),new()).Motivo);}
    [Fact] public void MissingAndOtherExecutionHaveExplicitCause()
    {Assert.Equal("SinPosicion",SelectorPosicion.Elegir([],Now,Now.AddHours(-1),new()).Motivo);Assert.Equal("GpsOtraEjecucion",SelectorPosicion.Elegir(new[]{P()},Now,Now,new()).Motivo);}
    [Fact] public void LimitsAreInclusiveConfigurableAndTiesDoNotDependOnReceiptOrder()
    {
        var p=P(120,100);Assert.Equal(p,SelectorPosicion.Elegir([p],Now,Now.AddHours(-1),new()).Posicion);
        Assert.Equal("GpsAntiguo",SelectorPosicion.Elegir([p],Now,Now.AddHours(-1),new(){AntiguedadMaximaSegundos=119}).Motivo);
        Assert.Equal("GpsImpreciso",SelectorPosicion.Elegir([P(10,80)],Now,Now.AddHours(-1),new(){PrecisionMaximaMetros=70}).Motivo);
        var precise=P(10,5);var imprecise=P(10,50);Assert.Equal(precise,SelectorPosicion.Elegir([imprecise,precise],Now,Now.AddHours(-1),new()).Posicion);
        Assert.Equal(precise,SelectorPosicion.Elegir([precise,imprecise],Now,Now.AddHours(-1),new()).Posicion);
        var invalid=P() with{Punto=new(double.NaN,0)};Assert.Equal("GpsInvalido",SelectorPosicion.Elegir([invalid],Now,Now.AddHours(-1),new()).Motivo);
    }
    [Fact] public void TighteningPrecisionDoesNotAuthorizeRewindingAnAlreadyUsedMeasurement()
    {var older=P(20,10);var used=P(10,80);var selected=SelectorPosicion.Elegir([older,used],Now,Now.AddHours(-1),new(){PrecisionMaximaMetros=50},used.MedidaEnUtc);Assert.Null(selected.Posicion);}
}
