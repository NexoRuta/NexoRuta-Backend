using Microsoft.AspNetCore.Http;

namespace NexoRuta.Api.Features.Envios.Contracts.Requests;

public sealed class ImportarEnviosRequest
{
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.Range(2, 2)]
    public int? VersionFormato { get; set; }

    public IFormFile? Archivo { get; set; }
    public Guid? OperadorId { get; set; }
}
