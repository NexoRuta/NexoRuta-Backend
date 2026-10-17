using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Contracts.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegistroRequest
{
    [Required, StringLength(254)]
    public string Email { get; init; } = "";
    [Required, MinLength(12)]
    public string Password { get; init; } = "";
}
