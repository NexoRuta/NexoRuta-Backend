using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Results;

public sealed class SesionMovilIniciada(SesionIniciada identidad, SesionMovil sesion, string tokenRenovacion)
{
    public SesionIniciada Identidad { get; } = identidad;
    public SesionMovil Sesion { get; } = sesion;
    public string TokenRenovacion { get; } = tokenRenovacion;
}
