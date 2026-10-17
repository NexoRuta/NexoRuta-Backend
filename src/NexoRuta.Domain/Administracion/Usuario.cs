
namespace NexoRuta.Domain.Administracion;

public sealed class Usuario
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string? PasswordHash { get; private set; }
    public long Version { get; private set; }

    private Usuario() { } // EF Core

    public Usuario(string email, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException(
                "El correo electrónico es obligatorio.",
                nameof(email));

        Id = Guid.CreateVersion7();
        Email = email.Trim();

        ActualizarPasswordHash(passwordHash);
    }

    public void ActualizarPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException(
                "El hash de contraseña es obligatorio.",
                nameof(passwordHash));

        PasswordHash = passwordHash;
    }

    public bool CambiarEmail(string email)
    {
        if (string.Equals(Email, email, StringComparison.Ordinal)) return false;
        Email = email;
        Version = checked(Version + 1);
        return true;
    }
}
