
using Microsoft.AspNetCore.Authorization;

namespace NexoRuta.Api.Core.Security;

public sealed record PermisoRequirement(
    string Codigo) : IAuthorizationRequirement;
