namespace NexoRuta.Domain.Envios;

public sealed class Destinatario
{
    public Guid Id { get; private set; }
    // Aísla los datos del envío; el destinatario no tiene cuenta ni membresía en el operador.
    public Guid OperadorId { get; private set; }
    public string Nombre { get; private set; }
    public string Email { get; private set; }

    public Destinatario(Guid operadorId, string nombre, string email)
    {
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre es obligatorio.", nameof(nombre));
        var correo = email?.Trim();
        if (string.IsNullOrWhiteSpace(correo) || correo.Length > 254 || correo.Any(char.IsControl)
            || !System.Net.Mail.MailAddress.TryCreate(correo, out var direccion) || direccion.Address != correo)
            throw new ArgumentException("Ingresá un correo válido para el destinatario, de hasta 254 caracteres.", nameof(email));

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        Nombre = nombre.Trim();
        Email = correo;
    }
}
