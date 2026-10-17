using NexoRuta.Domain.Administracion;

namespace NexoRuta.UnitTests.Administracion;

public sealed class EstadoOrganizacionTests
{
    [Fact]
    public void ComercioStartsActiveAndStateChangesIncrementVersionOnlyWhenNeeded()
    {
        var commerce = new Comercio("Commerce");

        Assert.Equal(EstadoOrganizacion.Activa, commerce.Estado);
        Assert.True(commerce.CambiarEstado(EstadoOrganizacion.Suspendida));
        Assert.False(commerce.CambiarEstado(EstadoOrganizacion.Suspendida));
        Assert.Equal(1, commerce.Version);
        Assert.True(commerce.CambiarEstado(EstadoOrganizacion.Activa));
        Assert.Equal(2, commerce.Version);
    }

    [Fact]
    public void OperatorStartsActiveAndStateChangesIncrementVersionOnlyWhenNeeded()
    {
        var operatorEntity = new Operador("Operator");

        Assert.Equal(EstadoOrganizacion.Activa, operatorEntity.Estado);
        Assert.True(operatorEntity.CambiarEstado(EstadoOrganizacion.Suspendida));
        Assert.False(operatorEntity.CambiarEstado(EstadoOrganizacion.Suspendida));
        Assert.Equal(1, operatorEntity.Version);
        Assert.True(operatorEntity.CambiarEstado(EstadoOrganizacion.Activa));
        Assert.Equal(2, operatorEntity.Version);
    }

    [Fact]
    public void CancellationRequestCanBeResolvedOnceAndRejectsConflictingResolution()
    {
        var organizationId = Guid.CreateVersion7();
        var requesterId = Guid.CreateVersion7();
        var resolverId = Guid.CreateVersion7();
        var requestedAt = DateTimeOffset.UtcNow;
        var request = new SolicitudBajaOrganizacion(
            TipoAccesoUsuario.Comercio, organizationId, requesterId, requestedAt, "  cierre definitivo  ");

        Assert.Equal(EstadoSolicitudBaja.Pendiente, request.Estado);
        Assert.Equal("cierre definitivo", request.Motivo);
        Assert.True(request.Resolver(EstadoSolicitudBaja.Aprobada, resolverId, requestedAt.AddMinutes(1), "confirmada"));
        Assert.False(request.Resolver(EstadoSolicitudBaja.Aprobada, resolverId, requestedAt.AddMinutes(2), "confirmada"));
        Assert.Equal(EstadoSolicitudBaja.Aprobada, request.Estado);
        Assert.Equal(resolverId, request.ResueltaPorUsuarioId);
        Assert.Throws<InvalidOperationException>(() =>
            request.Resolver(EstadoSolicitudBaja.Rechazada, resolverId, requestedAt.AddMinutes(2), "incompatible"));
    }
}
