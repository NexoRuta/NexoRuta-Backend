using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Operacion;
using NexoRuta.Application.Operacion.Repositories;
using NexoRuta.Application.Operacion.Results;
using NexoRuta.Application.Operacion.UseCases;

namespace NexoRuta.UnitTests.Operacion;

public sealed class OperationalHistoryUseCaseTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(1);
    private static readonly Guid Operator = Guid.NewGuid();
    private static readonly DateTimeOffset Verified = From.AddDays(1);

    [Fact]
    public async Task OperatorReadsOnlyTheirOwnOperatorAndReturnsTypedResults()
    {
        var eventRow = Event(Operator);
        var repository = new FakeHistory { Rows = [eventRow] };
        var state = new FakeHealth(Health("Actualizada"), Health("Actualizada"));
        var useCase = History(Actor(operatorId: Operator), repository, state);

        var result = await useCase.EjecutarAsync(Operator, From, To, null, "Envio", 0, 50);

        Assert.Equal("Actualizada", result.EstadoProyeccion);
        Assert.Equal(PoliticaHistorialOperativo.RetencionDias, result.RetencionDias);
        Assert.Same(eventRow, Assert.Single(result.Datos));
        Assert.NotNull(repository.LastQuery);
        Assert.Equal(Operator, repository.LastQuery.Value.OperatorId);
        Assert.Null(repository.LastQuery.Value.CommerceId);
        Assert.Equal("Envio", repository.LastQuery.Value.Type);
        Assert.Equal(From, repository.LastQuery.Value.From);
    }

    [Fact]
    public async Task CommerceScopeIsDerivedFromTheAuthenticatedActor()
    {
        var commerce = Guid.NewGuid();
        var repository = new FakeHistory();
        var useCase = History(Actor(commerceId: commerce), repository,
            new FakeHealth(Health("Actualizada"), Health("Actualizada")));

        await useCase.EjecutarAsync(Operator, From, To, null, null, 0, 50);

        Assert.Equal(commerce, repository.LastQuery?.CommerceId);
        Assert.Equal(Operator, repository.LastQuery?.OperatorId);
    }

    [Fact]
    public async Task OperatorCannotQueryAnotherOperator()
    {
        var useCase = History(Actor(operatorId: Operator), new FakeHistory(),
            new FakeHealth(Health("Actualizada"), Health("Actualizada")));

        await Assert.ThrowsAsync<AccesoNoPermitidoException>(() =>
            useCase.EjecutarAsync(Guid.NewGuid(), From, To, null, null, 0, 50));
    }

    [Theory]
    [InlineData(-1, 50, "Envio")]
    [InlineData(10001, 50, "Envio")]
    [InlineData(0, 0, "Envio")]
    [InlineData(0, 101, "Envio")]
    [InlineData(0, 50, "Unknown")]
    public async Task InvalidPaginationAndEventTypesAreRejectedBeforeAnyIO(int offset, int limit, string type)
    {
        var repository = new FakeHistory();
        var state = new FakeHealth(Health("Actualizada"), Health("Actualizada"));
        var useCase = History(Actor(operatorId: Operator), repository, state);
        await Assert.ThrowsAsync<ArgumentException>(() => useCase.EjecutarAsync(Operator, From, To,
            null, type, offset, limit));

        Assert.Null(repository.LastQuery);
        Assert.Equal(0, state.Reads);
    }

    [Fact]
    public async Task ProjectionMustBeCurrentBeforeAndAfterTheReadToReportUpdated()
    {
        var useCase = History(Actor(operatorId: Operator), new FakeHistory(),
            new FakeHealth(Health("Indisponible"), Health("Actualizada")));

        var result = await useCase.EjecutarAsync(Operator, From, To, null, null, 0, 50);

        Assert.Equal("Indisponible", result.EstadoProyeccion);
    }

    [Fact]
    public async Task RangeIsHalfOpenAndCannotExceedThirtyOneDays()
    {
        var useCase = History(Actor(operatorId: Operator), new FakeHistory(),
            new FakeHealth(Health("Actualizada"), Health("Actualizada")));

        await Assert.ThrowsAsync<ArgumentException>(() => useCase.EjecutarAsync(Operator, From, From, null, null, 0, 50));
        await Assert.ThrowsAsync<ArgumentException>(() => useCase.EjecutarAsync(Operator, From, From.AddDays(31).AddTicks(1), null, null, 0, 50));
        var result = await useCase.EjecutarAsync(Operator, From, From.AddDays(31), null, null, 0, 50);
        Assert.Empty(result.Datos);
    }

    private static ConsultarHistorialOperativoUseCase History(
        ContextoUsuario actor, FakeHistory history, FakeHealth health)
        => new(new ActorFake(actor), history, health);

    private static ContextoUsuario Actor(Guid? operatorId = null, Guid? commerceId = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), operatorId, commerceId, "actor@example.test",
            "Operator", commerceId is null ? null : "Commerce", Guid.NewGuid(), "reader");

    private static EstadoProyeccionOperativa Health(string status)
        => new(5, 5, status, Verified);

    private static EventoOperativo Event(Guid operatorId)
    {
        var shipment = Guid.NewGuid();
        return new(1, Guid.NewGuid(), operatorId, Guid.NewGuid(), "Envio", shipment, shipment,
            null, 1, Guid.NewGuid(), null, From, System.Text.Json.JsonSerializer.SerializeToElement(new { estado = "Admitido" }));
    }

    private sealed class FakeHistory : IHistorialOperativoRepository
    {
        public IReadOnlyList<EventoOperativo> Rows { get; init; } = [];
        public (Guid OperatorId, Guid? CommerceId, Guid? ShipmentId, string? Type,
            DateTimeOffset From, DateTimeOffset To, int Offset, int Limit)? LastQuery { get; private set; }

        public Task<IReadOnlyList<EventoOperativo>> QueryAsync(Guid operatorId, Guid? commerceId,
            Guid? shipmentId, string? type, DateTimeOffset from, DateTimeOffset to,
            int offset, int limit, CancellationToken ct)
        {
            LastQuery = (operatorId, commerceId, shipmentId, type, from, to, offset, limit);
            return Task.FromResult(Rows);
        }

        public Task<IReadOnlyDictionary<string, long>> VolumesAsync(Guid operatorId, Guid? commerceId,
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    }

    private sealed class FakeHealth(EstadoProyeccionOperativa before, EstadoProyeccionOperativa after)
        : IEstadoProyeccionOperativaRepository
    {
        public int Reads { get; private set; }

        public Task<EstadoProyeccionOperativa> ReadAsync(Guid operatorId, Guid? commerceId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Reads == 1 ? before : after);
        }
    }

    private sealed class ActorFake(ContextoUsuario context) : IUsuarioActual
    {
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
