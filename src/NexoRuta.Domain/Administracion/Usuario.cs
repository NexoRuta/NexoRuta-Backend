namespace NexoRuta.Domain.Administracion;

public sealed class Usuario
{
    public Guid Id { get; private set; }
    public string Email { get; private set; }

    public Usuario(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("El correo electrónico es obligatorio.", nameof(email));

        Id = Guid.CreateVersion7();
        Email = email.Trim();
    }
}
