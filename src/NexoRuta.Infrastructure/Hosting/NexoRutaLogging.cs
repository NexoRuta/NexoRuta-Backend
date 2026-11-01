using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Core;
using Serilog.Configuration;

namespace NexoRuta.Infrastructure.Hosting;

public static class NexoRutaLogging
{
    public static IHostBuilder UseNexoRutaLogging(this IHostBuilder host, string serviceName)
        => host.UseSerilog((_, _, logger) => Configure(logger, serviceName));

    public static ILoggingBuilder UseNexoRutaLogging(this ILoggingBuilder logging, string serviceName)
    {
        logging.ClearProviders();
        logging.AddSerilog(Configure(new LoggerConfiguration(), serviceName).CreateLogger(), dispose: true);
        return logging;
    }

    private static LoggerConfiguration Configure(LoggerConfiguration logger, string serviceName)
    {
        var configured = logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .Enrich.WithActivityContext()
            .WriteTo.Console(new JsonFormatter(renderMessage: true));

        var localLogFile = Environment.GetEnvironmentVariable("NEXORUTA_LOCAL_LOG_FILE");
        if (!string.IsNullOrWhiteSpace(localLogFile))
        {
            configured = configured.WriteTo.File(
                new JsonFormatter(renderMessage: true),
                localLogFile,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 16 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7);
        }

        return configured;
    }

    private static LoggerConfiguration WithActivityContext(this LoggerEnrichmentConfiguration enrich)
        => enrich.With(new ActivityContextEnricher());

    private sealed class ActivityContextEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (Activity.Current is not { } activity)
                return;

            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("trace_id", activity.TraceId.ToHexString()));
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("span_id", activity.SpanId.ToHexString()));
        }
    }
}
