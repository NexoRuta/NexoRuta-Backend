using NexoRuta.Domain.Administracion;

namespace NexoRuta.UnitTests.Envios;

public sealed class AccesoUsuarioTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Acceso_RechazaPertenenciaAmbiguaOPropietarioDeOperador(bool operador, bool comercio, bool propietario)
        => Assert.Throws<ArgumentException>(() => new AccesoUsuario(
            usuarioId: Guid.CreateVersion7(),
            operadorId: operador ? Guid.CreateVersion7() : null,
            comercioId: comercio ? Guid.CreateVersion7() : null,
            esPropietario: propietario));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Acceso_RechazaIdentificadoresVacios(int vacio)
        => Assert.Throws<ArgumentException>(() => new AccesoUsuario(
            usuarioId: vacio == 0 ? Guid.Empty : Guid.CreateVersion7(),
            operadorId: vacio == 1 ? Guid.Empty : null,
            comercioId: vacio == 1 ? null : vacio == 2 ? Guid.Empty : Guid.CreateVersion7()));

    [Fact]
    public void Acceso_NoEsLaIdentidadDeLaPersona()
    {
        var usuarioId = Guid.CreateVersion7();
        var operador = new AccesoUsuario(usuarioId: usuarioId, operadorId: Guid.CreateVersion7(), comercioId: null);
        var comercio = new AccesoUsuario(usuarioId: usuarioId, operadorId: null, comercioId: Guid.CreateVersion7(), esPropietario: true);
        Assert.Equal(operador.UsuarioId, comercio.UsuarioId);
        Assert.NotEqual(operador.Id, comercio.Id);
        Assert.Null(operador.ComercioId);
        Assert.Null(comercio.OperadorId);
        Assert.True(comercio.EsPropietario);
    }
}
