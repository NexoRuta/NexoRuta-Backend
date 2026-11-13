using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NexoRuta.Api.Core.Health;

public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Status.ToString().ToLowerInvariant())
        }));
    }
}
