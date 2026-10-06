namespace NexoRuta.Domain.Envios;

public sealed class Bulto
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid EnvioId { get; private set; }
    public string Codigo { get; private set; }
    public decimal PesoGramos { get; private set; }
    public decimal LargoCentimetros { get; private set; }
    public decimal AnchoCentimetros { get; private set; }
    public decimal AltoCentimetros { get; private set; }

    public Bulto(
        Guid operadorId,
        Guid envioId,
        string codigo,
        decimal pesoGramos,
        decimal largoCentimetros,
        decimal anchoCentimetros,
        decimal altoCentimetros)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (envioId == Guid.Empty)
            throw new ArgumentException("El envío es obligatorio.", nameof(envioId));
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException("El código es obligatorio.", nameof(codigo));
        if (pesoGramos <= 0)
            throw new ArgumentOutOfRangeException(nameof(pesoGramos), "El peso debe ser positivo.");
        if (largoCentimetros <= 0)
            throw new ArgumentOutOfRangeException(nameof(largoCentimetros), "El largo debe ser positivo.");
        if (anchoCentimetros <= 0)
            throw new ArgumentOutOfRangeException(nameof(anchoCentimetros), "El ancho debe ser positivo.");
        if (altoCentimetros <= 0)
            throw new ArgumentOutOfRangeException(nameof(altoCentimetros), "El alto debe ser positivo.");

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        EnvioId = envioId;
        Codigo = codigo.Trim();
        PesoGramos = pesoGramos;
        LargoCentimetros = largoCentimetros;
        AnchoCentimetros = anchoCentimetros;
        AltoCentimetros = altoCentimetros;
    }
}
