using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace NexoRuta.Api.Features.IdentidadVisual.Contracts;

public sealed class ActualizarIdentidadVisualForm
{
    [Required, Range(0, int.MaxValue)]
    public int? VersionEsperada { get; set; }
    public string? NombreComercial { get; set; }
    public string? CorreoPublico { get; set; }
    public string? TelefonoPublico { get; set; }
    public string? ColorPrimario { get; set; }
    public string? ColorSecundario { get; set; }
    public bool QuitarLogo { get; set; }
    public IFormFile? Logo { get; set; }
}
