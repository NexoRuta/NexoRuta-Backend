using Microsoft.AspNetCore.Http;

namespace NexoRuta.Api.Features.Envios.Contracts.Requests;

public sealed class ImportarEnviosRequest
{
    public IFormFile? Archivo { get; set; }
    public Guid? OperadorId { get; set; }
}
