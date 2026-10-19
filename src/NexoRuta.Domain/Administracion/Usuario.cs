using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;

namespace NexoRuta.Domain.Administracion;

public sealed class Usuario : IdentityUser<Guid>
{
    public long Version { get; private set; }
    public long VersionCredencial { get; private set; }

    [AllowNull]
    public override string Email
    {
        get => base.Email!;
        set => base.Email = value;
    }

    private Usuario() { } // EF Core

    public Usuario(string email) : this()
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException(
                "El correo electrónico es obligatorio.",
                nameof(email));

        Id = Guid.CreateVersion7();
        Email = email.Trim();
        UserName = Email;
        NormalizedEmail = Email.ToUpperInvariant();
        NormalizedUserName = NormalizedEmail;
    }

    public Usuario(string email, string passwordHash) : this(email)
    {
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
        UserName = email;
        NormalizedEmail = email.ToUpperInvariant();
        NormalizedUserName = NormalizedEmail;
        Version = checked(Version + 1);
        return true;
    }

    public void RevocarSesionesPorCambioDePassword() => VersionCredencial = checked(VersionCredencial + 1);
}
