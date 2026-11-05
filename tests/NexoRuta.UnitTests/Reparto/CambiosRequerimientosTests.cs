using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.UnitTests.Reparto;
public sealed class CambiosRequerimientosTests
{
    [Theory]
    [InlineData(ResultadoBulto.Entregado,ResultadoBulto.Pendiente,EstadoEnvio.EntregadoParcialmente)]
    [InlineData(ResultadoBulto.Entregado,ResultadoBulto.Devuelto,EstadoEnvio.EntregadoParcialmente)]
    [InlineData(ResultadoBulto.Pendiente,ResultadoBulto.Devuelto,EstadoEnvio.EnTransito)]
    [InlineData(ResultadoBulto.Devuelto,ResultadoBulto.Devuelto,EstadoEnvio.Devuelto)]
    [InlineData(ResultadoBulto.Entregado,ResultadoBulto.EntregaPendienteValidacion,EstadoEnvio.EntregaPendienteValidacion)]
    [InlineData(ResultadoBulto.Entregado,ResultadoBulto.EntregaSinValidacion,EstadoEnvio.EntregaSinValidacion)]
    public void AgregacionNoPierdeEntregasNiConfundePendientesConDevueltos(ResultadoBulto a,ResultadoBulto b,EstadoEnvio esperado)
    {
        Assert.Equal(esperado,AgregacionBultos.Derivar([a,b],EstadoEnvio.EnTransito));
        Assert.Equal(esperado,AgregacionBultos.Derivar([b,a],EstadoEnvio.EnTransito));
    }
    [Theory]
    [InlineData(1500,100000)]
    [InlineData(100000,1500)]
    public void OrdenMixtoPermiteDescargarAntesDeRetirarYRechazaElOrdenContrario(decimal pesoMaximo,decimal volumenMaximo)
    {
        var op=Guid.NewGuid();var entrega=Guid.NewGuid();var retiro=Guid.NewGuid();
        var vehiculo=new Vehiculo(op,"SINTETICO",pesoMaximo,volumenMaximo);
        Bulto[] bultos=[new(op,entrega,"ENTREGA",1000,10,10,10),new(op,retiro,"RETIRO",1000,10,10,10)];
        Assert.Null(CapacidadCarga.ExcesoRecorrido(vehiculo,[(entrega,false),(retiro,true)],bultos));
        Assert.NotNull(CapacidadCarga.ExcesoRecorrido(vehiculo,[(retiro,true),(entrega,false)],bultos));
    }
}
