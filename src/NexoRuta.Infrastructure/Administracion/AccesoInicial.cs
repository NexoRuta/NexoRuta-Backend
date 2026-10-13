namespace NexoRuta.Infrastructure.Administracion;

public sealed record AccesoInicial(string UsuarioEmail, string OperadorNombre, string ComercioNombre, string OperadorUsuarioEmail)
{
    public Guid? OperadorId { get; set; }
    public Guid? ComercioId { get; set; }
    public string? OperadorRolCodigo { get; set; }
    public string? ComercioRolCodigo { get; set; }
}
