namespace NexoRuta.Infrastructure.Administracion;

public sealed record RolInicial
{
    public string Codigo { get; init; } = "";
    public string Nombre { get; init; } = "";
    public Guid? OperadorId { get; init; }
    public Guid? ComercioId { get; init; }
    public string[] Permisos { get; init; } = [];
}
