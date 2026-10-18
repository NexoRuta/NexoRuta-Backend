using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Contracts.Auth;

public sealed class SolicitarRecuperacionPasswordRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = "";
}
