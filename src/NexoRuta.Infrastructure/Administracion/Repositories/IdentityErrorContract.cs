using Microsoft.AspNetCore.Identity;
using NexoRuta.Application.Administracion.Exceptions;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public static class IdentityErrorContract
{
    public static bool EsDuplicado(IdentityResult result, IdentityErrorDescriber describer)
    {
        var emailCode = describer.DuplicateEmail(string.Empty).Code;
        var userNameCode = describer.DuplicateUserName(string.Empty).Code;
        return result.Errors.Any(error => error.Code == emailCode || error.Code == userNameCode);
    }

    public static void ExigirCreacionValida(IdentityResult result, IdentityErrorDescriber describer)
    {
        if (result.Succeeded) return;
        if (EsDuplicado(result, describer)) throw new CorreoYaRegistradoException();
        throw new CredencialesInvalidasException();
    }
}
