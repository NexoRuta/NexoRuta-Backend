using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Application.Administracion;

public static class ValidacionCredenciales
{
    public static string Validar(string email, string password)
    {
        var normalizado = NormalizarEmail(email);
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new ArgumentException("La contraseña debe tener al menos 12 caracteres.", nameof(password));
        return normalizado;
    }

    public static string NormalizarEmail(string email)
    {
        var normalizado = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizado) || normalizado.Length > 254 ||
            !new EmailAddressAttribute().IsValid(normalizado))
            throw new ArgumentException("El correo electrónico no es válido.", nameof(email));
        return normalizado;
    }
}
