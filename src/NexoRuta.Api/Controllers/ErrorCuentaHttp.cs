using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Exceptions;

namespace NexoRuta.Api.Controllers;

internal static class ErrorCuentaHttp
{
    public static ObjectResult Traducir(Exception error) => error switch
    {
        CorreoYaRegistradoException => new ConflictObjectResult(new { message = error.Message }),
        CredencialesInvalidasException => new BadRequestObjectResult(new
        {
            message = new ArgumentException("La cuenta no cumple las reglas de Identity.", "password").Message
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(error))
    };
}
