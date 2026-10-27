using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NexoRuta.Infrastructure.Hosting;

public static class HostingTelemetry
{
    public static IServiceCollection AddHostingTelemetry(
        this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        if (!configuration.GetValue<bool>("OpenTelemetry:Enabled"))
            return services;

        var ratio = configuration.GetValue<double?>("OpenTelemetry:SamplingRatio") ?? 0.1;
        if (!double.IsFinite(ratio) || ratio is < 0 or > 1)
            throw new InvalidOperationException("OpenTelemetry:SamplingRatio must be between 0 and 1.");

        var instance = configuration["NODE_NAME"] ?? configuration["Hosting:NodeName"] ?? Environment.MachineName;
        var release = configuration["RELEASE_ID"] ?? configuration["Hosting:ReleaseId"] ?? "local";
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName, serviceVersion: release, serviceInstanceId: instance))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("NexoRuta.Hosting")
                .AddOtlpExporter())
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)))
                .AddAspNetCoreInstrumentation(options => options.RecordException = false)
                .AddProcessor(new SafeRequestProcessor())
                .AddOtlpExporter());
        // Outbound HTTP spans are intentionally absent: provider URLs can contain credentials.
        return services;
    }

    public static IServiceCollection AddTrustedProxyHeaders(
        this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Hosting:BehindLoadBalancer"))
            return services;

        var trustedNetworks = new List<System.Net.IPNetwork>
        {
            System.Net.IPNetwork.Parse("35.191.0.0/16"),
            System.Net.IPNetwork.Parse("130.211.0.0/22")
        };
        foreach (var value in (configuration["Hosting:TrustedNetworks"] ?? "")
                     .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network) || network.PrefixLength == 0)
                throw new InvalidOperationException("Hosting:TrustedNetworks must contain explicit non-default CIDR networks.");
            trustedNetworks.Add(network);
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 2;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var network in trustedNetworks)
                options.KnownIPNetworks.Add(network);
        });
        return services;
    }

    private sealed class SafeRequestProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            var route = activity.GetTagItem("http.route") as string;
            var method = activity.GetTagItem("http.request.method") as string
                         ?? activity.GetTagItem("http.method") as string;
            method = method is { Length: > 0 and <= 16 } && method.All(char.IsAsciiLetter) ? method : "HTTP";
            activity.DisplayName = string.IsNullOrWhiteSpace(route) ? method : $"{method} {route}";

            foreach (var tag in activity.TagObjects.ToArray())
            {
                if (tag.Key.StartsWith("url.", StringComparison.Ordinal)
                    || tag.Key.StartsWith("exception.", StringComparison.Ordinal)
                    || tag.Key is "http.url" or "http.target" or "http.path" or "http.query_string"
                        or "http.request.uri")
                    activity.SetTag(tag.Key, null);
            }
            // Error descriptions may include provider URLs or user data.
            if (activity.Status == ActivityStatusCode.Error)
                activity.SetStatus(ActivityStatusCode.Error);
        }
    }
}
