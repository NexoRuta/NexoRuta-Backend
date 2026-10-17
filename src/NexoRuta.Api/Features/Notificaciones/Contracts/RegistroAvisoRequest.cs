using System.ComponentModel.DataAnnotations;
namespace NexoRuta.Api.Features.Notificaciones.Contracts;
public sealed class RegistroAvisoRequest { [Required, StringLength(20)] public string Tipo { get; init; } = ""; }
