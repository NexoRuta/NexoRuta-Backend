namespace NexoRuta.Domain.ReglasOperativas;

public sealed class MotivoNoEntregaVersion
{
    public Guid OperadorId { get; private set; }
    public Guid VersionId { get; private set; }
    public string Codigo { get; private set; } = string.Empty;
    public string Descripcion { get; private set; } = string.Empty;
    public bool Disponible { get; private set; }

    private MotivoNoEntregaVersion() { }

    internal static MotivoNoEntregaVersion Crear(Guid operadorId, Guid versionId,
        MotivoNoEntregaConfiguracion motivo)
    {
        ArgumentNullException.ThrowIfNull(motivo);
        if (operadorId == Guid.Empty || versionId == Guid.Empty)
            throw new ArgumentException("La pertenencia del motivo es obligatoria.");
        if (!CodigoValido(motivo.Codigo))
            throw new ArgumentException("El código debe usar letras mayúsculas ASCII, números o guion bajo.", nameof(motivo));
        var descripcion = motivo.Descripcion?.Trim();
        if (string.IsNullOrWhiteSpace(descripcion)
            || descripcion.Length > ReglasOperativas.MotiveDescriptionMaxLength
            || descripcion.Any(char.IsControl))
            throw new ArgumentException("La descripción del motivo no es válida.", nameof(motivo));
        return new()
        {
            OperadorId = operadorId,
            VersionId = versionId,
            Codigo = motivo.Codigo,
            Descripcion = descripcion,
            Disponible = motivo.Disponible
        };
    }

    internal MotivoNoEntregaVersion Copiar(Guid versionId)
        => Crear(OperadorId, versionId, new(Codigo, Descripcion, Disponible));

    internal void ActualizarDescripcionYDisponibilidad(string descripcion, bool disponible)
    {
        var normalized = descripcion?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > ReglasOperativas.MotiveDescriptionMaxLength
            || normalized.Any(char.IsControl))
            throw new ArgumentException("La descripción del motivo no es válida.", nameof(descripcion));
        Descripcion = normalized;
        Disponible = disponible;
    }

    private static bool CodigoValido(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ReglasOperativas.MotiveCodeMaxLength
            || value[0] is < 'A' or > 'Z') return false;
        return value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    }
}
