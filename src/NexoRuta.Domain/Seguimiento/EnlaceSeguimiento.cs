namespace NexoRuta.Domain.Seguimiento;

public sealed class EnlaceSeguimiento
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid EnvioId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreadoEnUtc { get; private set; }
    public DateTimeOffset ExpiraEnUtc { get; private set; }
    public DateTimeOffset? RevocadoEnUtc { get; private set; }

    // Constructor utilizado por Entity Framework Core
    private EnlaceSeguimiento() { }

    public EnlaceSeguimiento(
        Guid operadorId,
        Guid envioId,
        string tokenHash,
        DateTimeOffset creadoEnUtc,
        DateTimeOffset expiraEnUtc)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.");

        if (envioId == Guid.Empty)
            throw new ArgumentException("El envío es obligatorio.");

        if (string.IsNullOrWhiteSpace(tokenHash)
            || tokenHash.Length != 64
            || !tokenHash.All(Uri.IsHexDigit))
            throw new ArgumentException("El hash del token debe ser SHA-256 en hexadecimal.");

        if (creadoEnUtc.Offset != TimeSpan.Zero ||
            expiraEnUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Las fechas deben expresarse en UTC.");

        if (expiraEnUtc <= creadoEnUtc)
            throw new ArgumentException(
                "La expiración debe ser posterior a la creación.");

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        EnvioId = envioId;
        TokenHash = tokenHash.ToLowerInvariant();
        CreadoEnUtc = creadoEnUtc;
        ExpiraEnUtc = expiraEnUtc;
    }

    public bool EstaVigente(DateTimeOffset ahoraUtc)
    {
        return ahoraUtc >= CreadoEnUtc
            && ahoraUtc < ExpiraEnUtc
            && RevocadoEnUtc is null;
    }

    public void Revocar(DateTimeOffset ahoraUtc)
    {
        if (ahoraUtc < CreadoEnUtc)
            throw new ArgumentException(
                "No se puede revocar antes de su creación.");

        RevocadoEnUtc ??= ahoraUtc.ToUniversalTime();
    }
}