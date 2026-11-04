using Microsoft.AspNetCore.Routing;
namespace NexoRuta.Api.Core.Security;

internal static class AuthDiagnostics
{
    internal const string Header="X-NexoRuta-Auth-Trace";
    internal const string ReasonItem="NexoRuta.Auth.RejectionReason";
    internal static void Rejected(HttpContext context,ILogger logger,int status,string reason)
    {
        var id=Guid.NewGuid().ToString("N");context.Response.Headers[Header]=id;
        var route=(context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText??"unmatched";
        // No exception messages, credentials, claims, headers, query strings or request bodies.
        logger.LogInformation(new EventId(status==401?40101:40301,"AuthenticationDecision"),
            "Auth rejection Status={Status} Reason={Reason} AuthTrace={AuthTrace} ServerUtc={ServerUtc} Method={Method} Route={Route}",
            status,reason,id,DateTimeOffset.UtcNow,context.Request.Method,route);
    }
}
