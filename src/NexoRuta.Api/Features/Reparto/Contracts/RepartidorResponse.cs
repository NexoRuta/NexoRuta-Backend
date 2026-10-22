using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed record RepartidorResponse(Guid AccesoId, Guid UsuarioId, Guid OperadorId,
    string Email, string Nombre, bool Activo, long Version)
{
    public static RepartidorResponse Desde(RepartidorResumen datos) => new(datos.AccesoId, datos.UsuarioId,
        datos.OperadorId, datos.Email, datos.Nombre, datos.Activo, datos.Version);
}
