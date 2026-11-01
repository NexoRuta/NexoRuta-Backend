using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NexoRuta.Infrastructure.Outbox;

public static class OutboxTraceContext
{
    private const string TraceParentProperty = "_nexoruta_traceparent";

    public static string AddCurrentTraceParent(string payload)
    {
        var traceParent = Activity.Current?.Id;
        if (string.IsNullOrWhiteSpace(traceParent)
            || !ActivityContext.TryParse(traceParent, traceState: null, out _))
            return payload;

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.TryGetProperty(TraceParentProperty, out _))
            throw new InvalidDataException("The outbox payload is not a valid traceable object.");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteString(TraceParentProperty, traceParent);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string? ReadTraceParent(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(TraceParentProperty, out var property)
            && property.ValueKind == JsonValueKind.String
            && ActivityContext.TryParse(property.GetString(), traceState: null, out _)
                ? property.GetString()
                : null;
    }

    public static string BusinessPayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(TraceParentProperty, out _))
            return payload;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name != TraceParentProperty)
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
