
using Microsoft.AspNetCore.Identity;
using NexoRuta.Application.Administracion.Interfaces;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class VerificadorPassword(
    IPasswordHasher<string> passwordHasher) : IVerificadorPassword
{
    public bool Verificar(
        string email,
        string passwordHash,
        string password)
    {
        var resultado = passwordHasher.VerifyHashedPassword(
            email,
            passwordHash,
            password);

        return resultado is
            PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
