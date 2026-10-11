using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Worker.Messaging;
using Npgsql;

namespace NexoRuta.UnitTests.Seguimiento;

public sealed class GenerarSeguimientoRetryTests
{
    [Theory]
    [InlineData("40001", "transient")]
    [InlineData("40P01", "transient")]
    [InlineData("08006", "transient")]
    [InlineData("57P01", "transient")]
    [InlineData("23503", "processing")]
    [InlineData("23505", "processing")]
    [InlineData("28P01", "processing")]
    [InlineData("42601", "processing")]
    public void PostgreSql_ClasificaCodigoConcreto(string sqlState, string esperado)
    {
        var error = new PostgresException("Test failure", "ERROR", "ERROR", sqlState);
        Assert.Equal(esperado, GenerarSeguimientoConsumer.ClasificarError(error, true));
        Assert.Equal(esperado, GenerarSeguimientoConsumer.ClasificarError(
            new DbUpdateException("EF wrapper", error), true));
        Assert.Equal(esperado, GenerarSeguimientoConsumer.ClasificarError(
            new InvalidOperationException("EF strategy wrapper", new DbUpdateException("EF", error)), true));
    }

    [Theory]
    [InlineData("timeout", "transient")]
    [InlineData("network", "transient")]
    [InlineData("ef-timeout", "transient")]
    [InlineData("strategy-timeout", "transient")]
    [InlineData("strategy-unexpected", "processing")]
    [InlineData("ef", "processing")]
    [InlineData("ef-unexpected", "processing")]
    [InlineData("npgsql", "processing")]
    [InlineData("unexpected", "processing")]
    [InlineData("cancel", "processing")]
    public void Infraestructura_NoClasificaExcepcionesGenericasComoTransitorias(
        string tipo, string esperado)
    {
        Exception error = tipo switch
        {
            "timeout" => new TimeoutException(),
            "network" => new NpgsqlException("Network", new SocketException()),
            "ef-timeout" => new DbUpdateException("EF", new TimeoutException()),
            "strategy-timeout" => new InvalidOperationException("EF strategy", new TimeoutException()),
            "strategy-unexpected" => new InvalidOperationException("EF strategy", new Exception()),
            "ef" => new DbUpdateException(),
            "ef-unexpected" => new DbUpdateException("EF", new Exception()),
            "npgsql" => new NpgsqlException(),
            "cancel" => new OperationCanceledException(),
            _ => new Exception()
        };
        Assert.Equal(esperado, GenerarSeguimientoConsumer.ClasificarError(error, true));
    }

    [Theory]
    [InlineData(false, "invalid")]
    [InlineData(true, "processing")]
    public void ErrorDeContrato_SoloDuranteValidacion(bool validado, string esperado)
    {
        Assert.Equal(esperado, GenerarSeguimientoConsumer.ClasificarError(new JsonException(), validado));
        Assert.Equal(esperado, GenerarSeguimientoConsumer.ClasificarError(new InvalidDataException(), validado));
    }

    [Fact]
    public void FallosMixtos_SoloTresFallosDeProcesamientoAgotanPresupuesto()
    {
        var plan = GenerarSeguimientoConsumer.PlanificarFallo("processing", 0, 0);
        Assert.Equal((false, 1, 1), plan);
        plan = GenerarSeguimientoConsumer.PlanificarFallo("transient", plan.Reintentos, plan.FallosProcesamiento);
        Assert.Equal((false, 2, 1), plan);
        plan = GenerarSeguimientoConsumer.PlanificarFallo("processing", plan.Reintentos, plan.FallosProcesamiento);
        Assert.Equal((false, 3, 2), plan);
        plan = GenerarSeguimientoConsumer.PlanificarFallo("transient", plan.Reintentos, plan.FallosProcesamiento);
        Assert.Equal((false, 4, 2), plan);
        plan = GenerarSeguimientoConsumer.PlanificarFallo("processing", plan.Reintentos, plan.FallosProcesamiento);
        Assert.Equal((true, 4, 3), plan);
    }

    [Fact]
    public void ContratoInvalido_NoProgramaReintento()
        => Assert.Equal((true, 0, 0), GenerarSeguimientoConsumer.PlanificarFallo("invalid", 0, 0));

    [Fact]
    public void ContadoresMaximos_NoDesbordan()
    {
        Assert.Equal((false, int.MaxValue, int.MaxValue),
            GenerarSeguimientoConsumer.PlanificarFallo("transient", int.MaxValue, int.MaxValue));
        Assert.Equal((true, int.MaxValue, 3),
            GenerarSeguimientoConsumer.PlanificarFallo("processing", int.MaxValue, int.MaxValue));
    }
}
