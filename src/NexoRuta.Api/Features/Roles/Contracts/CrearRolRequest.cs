using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Roles.Contracts;

public sealed record CrearRolRequest([Required, MaxLength(80)] string Codigo, [Required, MaxLength(160)] string Nombre);
