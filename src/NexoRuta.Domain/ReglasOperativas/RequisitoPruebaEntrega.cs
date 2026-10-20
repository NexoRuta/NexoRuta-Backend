namespace NexoRuta.Domain.ReglasOperativas;

public sealed class RequisitoPruebaEntrega
{
    public Guid OperadorId { get; private set; }
    public Guid VersionId { get; private set; }
    public TipoPruebaEntrega Tipo { get; private set; }

    private RequisitoPruebaEntrega() { }

    internal static RequisitoPruebaEntrega Crear(Guid operadorId, Guid versionId, TipoPruebaEntrega tipo)
    {
        if (operadorId == Guid.Empty || versionId == Guid.Empty)
            throw new ArgumentException("La pertenencia del requisito es obligatoria.");
        if (!Enum.IsDefined(tipo)) throw new ArgumentException("El tipo de prueba no es válido.", nameof(tipo));
        return new() { OperadorId = operadorId, VersionId = versionId, Tipo = tipo };
    }

    internal RequisitoPruebaEntrega Copiar(Guid versionId) => Crear(OperadorId, versionId, Tipo);
}
