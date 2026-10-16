using NexoRuta.Domain.Administracion;

namespace NexoRuta.UnitTests.Administracion;

public sealed class RolTests
{
    [Fact]
    public void AssignmentRejectsAnotherCommerce()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var role = new Rol("dispatch", "Dispatch", null, first);
        Assert.Throws<InvalidOperationException>(() => new AccesoUsuario(Guid.NewGuid(), null, second, role));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RoleRequiresExactlyOneOwner(bool op, bool commerce)
        => Assert.Throws<ArgumentException>(() => new Rol("dispatch", "Dispatch", op ? Guid.NewGuid() : null, commerce ? Guid.NewGuid() : null));

    [Fact]
    public void OwnerAndCodeRemainStableWhenRenaming()
    {
        var owner = Guid.NewGuid();
        var role = new Rol("  custom_role  ", "Custom", null, owner);
        role.Renombrar("  Edited  ");
        Assert.Equal("Edited", role.Nombre);
        Assert.Equal("custom_role", role.Codigo);
        Assert.Equal(owner, role.ComercioId);
        Assert.Null(role.OperadorId);
        Assert.Equal(owner, role.OrganizacionId);
        Assert.Throws<ArgumentException>(() => role.Renombrar(""));
    }

    [Fact]
    public void SystemProtectionIsOperatorOnlyAndBlocksDomainRename()
    {
        var role = new Rol("administrator", "Administrator", Guid.NewGuid(), null);
        role.ProtegerComoRolSistema();
        Assert.True(role.EsProtegidoSistema);
        Assert.Throws<InvalidOperationException>(() => role.Renombrar("Changed"));

        var commerceRole = new Rol("owner", "Owner", null, Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => commerceRole.ProtegerComoRolSistema());
        Assert.False(commerceRole.EsProtegidoSistema);
    }

    [Fact]
    public void RemovingRoleRetainsAccessAndIdentity()
    {
        var role = new Rol("custom", "Custom", Guid.NewGuid(), null);
        var userId = Guid.NewGuid();
        var access = new AccesoUsuario(userId, role.OperadorId, null, role);
        var id = access.Id;
        access.RetirarRol();
        Assert.Null(access.RolId);
        Assert.Equal(id, access.Id);
        Assert.Equal(userId, access.UsuarioId);
        Assert.Equal(role.OperadorId, access.OperadorId);
    }
}
