
using Microsoft.AspNetCore.Identity;
using NexoRuta.Application.Administracion.Interfaces;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class GeneradorPasswordHash(
    IPasswordHasher<string> hasher) : IGeneradorPasswordHash
{
    public string Generar(string email, string password)
        => hasher.HashPassword(email, password);
}
