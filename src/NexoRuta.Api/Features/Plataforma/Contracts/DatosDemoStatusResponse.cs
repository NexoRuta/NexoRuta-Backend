using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Api.Features.Plataforma.Contracts;

public sealed record DatosDemoStatusResponse(
    bool Sembrada,
    DateTimeOffset? SembradoEnUtc,
    int Operadores,
    int Comercios,
    int CuentasOperador,
    int CuentasComercio,
    int Envios,
    int Rutas,
    int Operaciones)
{
    public static DatosDemoStatusResponse Desde(EstadoDatosDemo estado) => new(
        estado.Sembrada, estado.SembradoEnUtc, estado.Operadores, estado.Comercios,
        estado.CuentasOperador, estado.CuentasComercio, estado.Envios, estado.Rutas, estado.Operaciones);
}
