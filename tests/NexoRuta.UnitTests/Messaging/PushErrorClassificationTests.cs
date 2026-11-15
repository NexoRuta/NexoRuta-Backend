using FirebaseAdmin.Messaging;
using NexoRuta.Worker.Messaging;

namespace NexoRuta.UnitTests.Messaging;

public sealed class PushErrorClassificationTests
{
    [Theory]
    [InlineData(MessagingErrorCode.InvalidArgument, CategoriaFalloPush.Mensaje)]
    [InlineData(MessagingErrorCode.SenderIdMismatch, CategoriaFalloPush.Configuracion)]
    [InlineData(MessagingErrorCode.ThirdPartyAuthError, CategoriaFalloPush.Configuracion)]
    [InlineData(MessagingErrorCode.QuotaExceeded, CategoriaFalloPush.Cuota)]
    [InlineData(MessagingErrorCode.Unavailable, CategoriaFalloPush.Transitorio)]
    [InlineData(MessagingErrorCode.Internal, CategoriaFalloPush.Transitorio)]
    public void FallaFcmSeClasificaSinPerderSuCodigo(MessagingErrorCode codigo, CategoriaFalloPush categoria)
    {
        var resultado = ResultadoEnvioPush.DesdeCodigoError(codigo);

        Assert.Equal(EstadoEnvioPush.Reintentar, resultado.Estado);
        Assert.Equal(categoria, resultado.Categoria);
        Assert.Equal(codigo.ToString(), resultado.CodigoError);
    }

    [Fact]
    public void SoloUnregisteredInvalidaLaInstalacion()
    {
        var resultado = ResultadoEnvioPush.DesdeCodigoError(MessagingErrorCode.Unregistered);

        Assert.Equal(EstadoEnvioPush.RegistroInvalido, resultado.Estado);
        Assert.Equal("Unregistered", resultado.CodigoError);
    }

    [Fact]
    public void UnregisteredDeLoteNoInvalidaTodosLosDestinos()
    {
        var resultado = ResultadoEnvioPush.DesdeCodigoErrorDeLote(MessagingErrorCode.Unregistered);

        Assert.Equal(EstadoEnvioPush.Reintentar, resultado.Estado);
        Assert.Equal(CategoriaFalloPush.Desconocido, resultado.Categoria);
        Assert.Equal("UnregisteredBatch", resultado.CodigoError);
    }

    [Fact]
    public void CodigoAusenteSeConservaComoErrorDesconocido()
    {
        var resultado = ResultadoEnvioPush.DesdeCodigoError(null);

        Assert.Equal(EstadoEnvioPush.Reintentar, resultado.Estado);
        Assert.Equal(CategoriaFalloPush.Desconocido, resultado.Categoria);
        Assert.Equal("Unknown", resultado.CodigoError);
    }
}
