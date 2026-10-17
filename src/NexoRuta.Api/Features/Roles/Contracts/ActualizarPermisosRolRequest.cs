using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Roles.Contracts;

public sealed record ActualizarPermisosRolRequest([Required] string[] Permisos);
