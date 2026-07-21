using System.Text.Json;

namespace LtlTool.Api.Features.Integrations.Alvys.Webhooks;

/// <summary>
/// Best-effort extraction of the load number from a webhook body. Alvys carries the full changed-load
/// snapshot at <c>data.load</c>; the load number is <c>data.load.LoadNumber</c>. Parsing is defensive —
/// a body that does not match the expected shape yields <c>null</c> rather than throwing, so a
/// malformed payload becomes a surfaced processing failure, never an unhandled crash of the processor.
/// This reads the load <b>number</b> only, never any operational value — Alvys stays authoritative.
/// </summary>
public static class AlvysWebhookPayload
{
    public static string? TryExtractLoadNumber(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return null;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
                return null;

            if (!data.TryGetProperty("load", out var load) || load.ValueKind != JsonValueKind.Object)
                return null;

            // Property lookup is case-insensitive against the canonical Alvys "LoadNumber" spelling.
            foreach (var name in new[] { "LoadNumber", "loadNumber" })
            {
                if (load.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    var loadNumber = value.GetString();
                    return string.IsNullOrWhiteSpace(loadNumber) ? null : loadNumber.Trim();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
