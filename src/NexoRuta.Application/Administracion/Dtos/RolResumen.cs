namespace NexoRuta.Application.Administracion.Dtos;

public sealed record RolResumen(Guid Id, string Codigo, string Nombre, IReadOnlyList<string> Permisos)
{
    public bool EsProtegidoSistema { get; init; }
    public bool EsPropio { get; init; }
}
