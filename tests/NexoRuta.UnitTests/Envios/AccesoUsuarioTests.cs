using NexoRuta.Domain.Administracion;

namespace NexoRuta.UnitTests.Envios;

public sealed class AccesoUsuarioTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Acceso_RechazaPertenenciaAmbigua(bool operador, bool comercio)
        => Assert.Throws<ArgumentException>(() => new AccesoUsuario(
            usuarioId: Guid.CreateVersion7(),
            operadorId: operador ? Guid.CreateVersion7() : null,
            comercioId: comercio ? Guid.CreateVersion7() : null,
            rol: new Rol("test", "Test", null, Guid.NewGuid())));

    [Fact]
    public void Acceso_RechazaRolDePropietarioParaOperador()
        => Assert.Throws<InvalidOperationException>(() => new AccesoUsuario(
            Guid.CreateVersion7(), Guid.CreateVersion7(), null,
            new Rol("propietario_comercio", "Owner", null, Guid.NewGuid())));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Acceso_RechazaIdentificadoresVacios(int vacio)
        => Assert.Throws<ArgumentException>(() => new AccesoUsuario(
            usuarioId: vacio == 0 ? Guid.Empty : Guid.CreateVersion7(),
            operadorId: vacio == 1 ? Guid.Empty : null,
            comercioId: vacio == 1 ? null : vacio == 2 ? Guid.Empty : Guid.CreateVersion7(),
            rol: new Rol("test", "Test", null, Guid.NewGuid())));

    [Fact]
    public void Acceso_NoEsLaIdentidadDeLaPersona()
    {
        var usuarioId = Guid.CreateVersion7();
        var rolOperador = new Rol("administrador_operador", "Administrator", Guid.NewGuid(), null);
        var rolComercio = new Rol("propietario_comercio", "Owner", null, Guid.NewGuid());
        var operador = new AccesoUsuario(usuarioId: usuarioId, operadorId: rolOperador.OperadorId, comercioId: null, rol: rolOperador);
        var comercio = new AccesoUsuario(usuarioId: usuarioId, operadorId: null, comercioId: rolComercio.ComercioId, rol: rolComercio);
        Assert.Equal(operador.UsuarioId, comercio.UsuarioId);
        Assert.NotEqual(operador.Id, comercio.Id);
        Assert.Null(operador.ComercioId);
        Assert.Null(comercio.OperadorId);
        Assert.Equal(rolComercio.Id, comercio.RolId);
        Assert.Equal(rolOperador.Id, operador.RolId);
    }
}
