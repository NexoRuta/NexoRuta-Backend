namespace NexoRuta.Application.Administracion.Context;

public sealed record ContextoPlataforma(Guid UsuarioId, Guid AccesoId, long VersionSesion,
    bool PuedeGestionarOrganizaciones, string UsuarioEmail = "");
