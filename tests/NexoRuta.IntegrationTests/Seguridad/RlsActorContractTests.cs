using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Persistence.Migrations;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class RlsActorContractTests
{
    [Fact]
    public void ActorSqlValuesAreExhaustiveAndMatchHistoricalPolicySql()
    {
        var expected = new Dictionary<RlsActorKind, string>
        {
            [RlsActorKind.Comercio] = "comercio",
            [RlsActorKind.Operador] = "operador",
            [RlsActorKind.Plataforma] = "plataforma",
            [RlsActorKind.Identidad] = "identidad",
            [RlsActorKind.Suspendida] = "suspendida",
            [RlsActorKind.Bootstrap] = "bootstrap",
            [RlsActorKind.Worker] = "worker"
        };
        Assert.Equal(expected.Keys.Order(), Enum.GetValues<RlsActorKind>().Order());
        var sql = HistoricalPolicySql();
        foreach (var (kind, value) in expected)
        {
            Assert.Equal(value, RlsContextContract.ActorSqlValue(kind));
            Assert.Contains($"'{value}'", sql, StringComparison.Ordinal);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RlsContextContract.ActorSqlValue((RlsActorKind)int.MaxValue));
    }

    [Fact]
    public void OutboxMessageTypeContractMatchesHistoricalPolicySql()
    {
        var sql = HistoricalPolicySql();
        Assert.Equal("GenerarSeguimiento", OutboxMessageTypes.TrackingRequested);
        Assert.Equal("EnvioCreado", OutboxMessageTypes.ShipmentCreated);
        Assert.Contains("'GenerarSeguimiento'", sql, StringComparison.Ordinal);
        Assert.Contains("'EnvioCreado'", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TipoAccesoUsuario.Comercio, "Comercio")]
    [InlineData(TipoAccesoUsuario.Operador, "Operador")]
    public void OrganizationKindsKeepSqlCasing(TipoAccesoUsuario kind, string expected)
    {
        Assert.Equal(expected, RlsContextContract.OrganizationSqlValue(kind));
        Assert.Contains($"'{expected}'", HistoricalPolicySql(), StringComparison.Ordinal);
        var context = new ContextoOrganizacionSuspendida(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, "Suspended organization", 1);
        var actor = RlsActorContext.From(context);
        Assert.Equal(RlsActorKind.Suspendida, actor.ActorKind);
        Assert.Equal(context.OrganizacionId, actor.OrganizationId);
        Assert.Equal(kind, actor.OrganizationType);
    }

    [Fact]
    public void ContextKeysKeepTheExplicitHistoricalSqlContract()
    {
        string[] expected =
        [
            "nexoruta.actor_kind", "nexoruta.user_id", "nexoruta.access_id", "nexoruta.org_id",
            "nexoruta.platform_manage_organizations", "nexoruta.bootstrap_operator_id",
            "nexoruta.bootstrap_commerce_id", "nexoruta.target_org_type", "nexoruta.job_message_id",
            "nexoruta.job_consumer", "nexoruta.job_shipment_id", "nexoruta.processor_id",
            "nexoruta.selected_operator_id", "nexoruta.shipment_id", "nexoruta.recipient_id",
            "nexoruta.address_id", "nexoruta.parcel_id", "nexoruta.selected_org_id",
            "nexoruta.target_user_id", "nexoruta.target_role_id", "nexoruta.auth_access_id",
            "nexoruta.auth_user_id", "nexoruta.auth_org_id", "nexoruta.auth_role_id"
        ];
        var actual = typeof(RlsContextContract).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!).ToArray();
        Assert.Equal(expected.Order(), actual.Order());
        var sql = HistoricalPolicySql();
        // processor_id is retained runtime metadata; no historical policy consumes it.
        foreach (var key in actual.Where(key => key != RlsContextContract.ProcessorId))
            Assert.True(sql.Contains($"'{key}'", StringComparison.Ordinal) ||
                        sql.Contains($"'{key["nexoruta.".Length..]}'", StringComparison.Ordinal), key);
    }

    [Fact]
    public void ActorContextRejectsUnknownOrganizationKinds()
    {
        var context = new ContextoOrganizacionSuspendida(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            (TipoAccesoUsuario)int.MaxValue, "Suspended organization", 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => RlsActorContext.From(context));
    }

    [Fact]
    public void CommonActorContextHasClosedKindAndNoOperationFields()
    {
        Assert.True(typeof(RlsActorContext).GetProperty(nameof(RlsActorContext.ActorKind))!.PropertyType.IsEnum);
        string[] operationFields =
        [
            "BootstrapOperatorId", "BootstrapCommerceId", "SelectedOperatorId", "ShipmentId",
            "JobMessageId", "JobShipmentId", "ProcessorId", "SelectedOrganizationId",
            "TargetUserId", "TargetRoleId", "JobConsumer"
        ];
        Assert.All(operationFields, field => Assert.Null(typeof(RlsActorContext).GetProperty(field)));
    }

    [Theory]
    [InlineData(true, RlsActorKind.Comercio, TipoAccesoUsuario.Comercio)]
    [InlineData(false, RlsActorKind.Operador, TipoAccesoUsuario.Operador)]
    public void UserFactoryPreservesActorAndOrganization(bool commerce, RlsActorKind kind, TipoAccesoUsuario organizationType)
    {
        var organizationId = Guid.NewGuid();
        var user = new ContextoUsuario(Guid.NewGuid(), Guid.NewGuid(),
            commerce ? null : organizationId, commerce ? organizationId : null,
            "user@example.test", "Operator", "Commerce", Guid.NewGuid(), "ADMIN");

        var actor = RlsActorContext.From(user);

        Assert.Equal(kind, actor.ActorKind);
        Assert.Equal(organizationId, actor.OrganizationId);
        Assert.Equal(user.UsuarioId, actor.UserId);
        Assert.Equal(user.AccesoId, actor.AccessId);
        Assert.Equal(organizationType, actor.OrganizationType);
    }

    [Fact]
    public void PlatformIdentityBootstrapAndWorkerFactoriesPreserveTheirSeparateData()
    {
        var userId = Guid.NewGuid();
        var accessId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var commerceId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var platform = RlsActorContext.From(new ContextoPlataforma(userId, accessId, 1, true));
        Assert.Equal(RlsActorKind.Plataforma, platform.ActorKind);
        Assert.Equal(userId, platform.UserId);
        Assert.Equal(accessId, platform.AccessId);
        Assert.True(platform.CanManageOrganizations);
        var identity = RlsActorContext.From(new IdentidadSinOrganizacion(userId, "user@example.test"));
        Assert.Equal(RlsActorKind.Identidad, identity.ActorKind);
        Assert.Equal(userId, identity.UserId);
        var bootstrap = RlsActorContext.Bootstrap(organizationId, commerceId);
        Assert.Equal(RlsActorKind.Bootstrap, bootstrap.ActorKind);
        Assert.Equal(organizationId, bootstrap.OperatorId);
        Assert.Equal(commerceId, bootstrap.CommerceId);
        var worker = RlsActorContext.Worker(organizationId, messageId, shipmentId, processorId);
        Assert.Equal(RlsActorKind.Worker, worker.ActorKind);
        Assert.Equal(organizationId, worker.OrganizationId);
        Assert.Equal(messageId, worker.MessageId);
        Assert.Equal(shipmentId, worker.ShipmentId);
        Assert.Equal(processorId, worker.ProcessorId);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RlsContextContract.OrganizationSqlValue((TipoAccesoUsuario)int.MaxValue));
    }

    private static string HistoricalPolicySql() => string.Join("\n",
        new EnablePostgreSqlRls().UpOperations.OfType<SqlOperation>().Select(operation => operation.Sql));
}
