using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace NexoRuta.Infrastructure.Hosting;

public static class NexoRutaRequestLogging
{
    public static IApplicationBuilder UseNexoRutaRequestLogging(this IApplicationBuilder app)
        => app.UseMiddleware<NexoRutaRequestLoggingMiddleware>();
}

internal sealed class NexoRutaRequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<NexoRutaRequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        catch (Exception error)
        {
            Log(context, StatusCodes.Status500InternalServerError, startedAt, error.GetType().Name);
            throw;
        }

        Log(context, context.Response.StatusCode, startedAt, null);
    }

    private void Log(HttpContext context, int statusCode, long startedAt, string? errorType)
    {
        var method = context.Request.Method;
        if (method.Length is 0 or > 16 || !method.All(char.IsAsciiLetter))
            method = "HTTP";

        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        route = string.IsNullOrWhiteSpace(route) ? "unmatched" : route;
        var durationMilliseconds = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 1);
        var level = errorType is not null || statusCode >= StatusCodes.Status500InternalServerError
            ? LogLevel.Error
            : statusCode >= StatusCodes.Status400BadRequest ? LogLevel.Warning : LogLevel.Information;

        logger.Log(level,
            "HTTP {RequestMethod} {Route} completed with {StatusCode} in {DurationMilliseconds} ms. ErrorType: {ErrorType}",
            method, route, statusCode, durationMilliseconds, errorType ?? "none");
    }
}
