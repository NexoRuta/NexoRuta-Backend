using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Roles.Contracts;

public sealed record ActualizarRolRequest([Required, MaxLength(160)] string Nombre);
