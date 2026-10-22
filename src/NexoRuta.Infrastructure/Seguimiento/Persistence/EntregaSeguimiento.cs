
namespace NexoRuta.Infrastructure.Seguimiento.Persistence;

public sealed class EntregaSeguimiento
{
    public Guid Id { get; private set; }

    public Guid OperadorId { get; private set; }

    public Guid EnvioId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public byte[] TokenCifrado { get; private set; } = [];

    public byte[] Nonce { get; private set; } = [];

    public byte[] Tag { get; private set; } = [];

    public DateTimeOffset CreadoEnUtc { get; private set; }

    private EntregaSeguimiento() { }

    public EntregaSeguimiento(
        Guid operadorId,
        Guid envioId,
        string tokenHash,
        byte[] tokenCifrado,
        byte[] nonce,
        byte[] tag)
    {
        if (operadorId == Guid.Empty || envioId == Guid.Empty)
            throw new ArgumentException(
                "El operador y el envío son obligatorios.");

        if (string.IsNullOrWhiteSpace(tokenHash) ||
            tokenHash.Length != 64)
            throw new ArgumentException(
                "El hash del token debe tener 64 caracteres.");

        if (tokenCifrado.Length == 0 ||
            nonce.Length != 12 ||
            tag.Length != 16)
            throw new ArgumentException(
                "Los datos del token cifrado no son válidos.");

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        EnvioId = envioId;
        TokenHash = tokenHash;

        TokenCifrado = tokenCifrado.ToArray();
        Nonce = nonce.ToArray();
        Tag = tag.ToArray();

        CreadoEnUtc = DateTimeOffset.UtcNow;
    }
}
