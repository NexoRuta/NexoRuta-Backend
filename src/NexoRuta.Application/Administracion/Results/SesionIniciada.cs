
namespace NexoRuta.Application.Administracion.Results;

public sealed record SesionIniciada(
    Guid UsuarioId,
    Guid? AccesoId,
    string Email,
    long VersionSesion,
    bool EsPlataforma = false)
{
    public bool SinOrganizacion => AccesoId is null && !EsPlataforma;
}
