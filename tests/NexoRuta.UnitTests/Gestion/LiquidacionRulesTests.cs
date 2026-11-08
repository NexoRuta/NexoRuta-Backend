using NexoRuta.Domain.Gestion;
namespace NexoRuta.UnitTests.Gestion;
public sealed class LiquidacionRulesTests
{
    [Theory]
    [InlineData("Borrador",true,"Confirmada")]
    [InlineData("Borrador",false,"Descartada")]
    [InlineData("Confirmada",true,"Confirmada")]
    [InlineData("Descartada",false,"Descartada")]
    public void CierreYReintentoDelMismoComando(string estado,bool confirmar,string esperado)
        => Assert.Equal(esperado,ReglasGestion.Transicion(estado,confirmar));
    [Theory]
    [InlineData("Confirmada",false)]
    [InlineData("Descartada",true)]
    public void DocumentoHistoricoNoSeReabreNiSeDescarta(string estado,bool confirmar)
        => Assert.Throws<ConflictoLiquidacionException>(()=>ReglasGestion.Transicion(estado,confirmar));
    [Theory]
    [InlineData(-10.25,true)]
    [InlineData(10.25,true)]
    [InlineData(0,false)]
    [InlineData(.001,false)]
    public void CorreccionesConSignoYPrecisionMonetaria(decimal amount,bool valid)
    {
        Action action=()=>ReglasGestion.ValidarCorreccion(Guid.NewGuid(),Guid.NewGuid(),amount,"Diferencia comprobada por revisión.");
        if(valid) action();else Assert.Throws<ArgumentException>(action);
    }
}
