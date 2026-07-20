using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LtlTool.Api.Features.Ltl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Azure OpenAI-backed <see cref="IAccessorialSignalExtractor"/>. Active only when
/// <c>Ltl:AccessorialAi:Enabled = true</c> AND the endpoint/deployment/key are all configured.
/// When any credential is absent the extractor degrades to empty signals — the deterministic
/// keyword extraction in <c>AccessorialSignalAnalyzer</c> still runs regardless.
///
/// <para>
/// Guardrails (non-negotiable):
/// <list type="bullet">
/// <item>Credentials are server-side only — never in SPA, source, tests, or docs.</item>
/// <item>The extractor is a signal-extraction layer only. It never prices, never asserts dollar
/// amounts, and never invents evidence outside the supplied text.</item>
/// <item>Any LLM failure degrades silently to empty signals; deterministic signals still surface.</item>
/// <item>Alvys read-only posture: this class reads text already fetched from Alvys notes/documents.
/// It makes no direct Alvys API calls.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AzureOpenAiAccessorialSignalExtractor(
    HttpClient http,
    IOptions<LtlOptions> options,
    ILogger<AzureOpenAiAccessorialSignalExtractor> logger) : IAccessorialSignalExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccessorialAiOptions _ai = options.Value.AccessorialAi;

    public bool IsEnabled => _ai.Enabled
        && !string.IsNullOrWhiteSpace(_ai.Endpoint)
        && !string.IsNullOrWhiteSpace(_ai.DeploymentName)
        && !string.IsNullOrWhiteSpace(_ai.ApiKey);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AccessorialSignal>> ExtractAsync(
        string sourceId, string sourceType, string text, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(text)) return [];

        try
        {
            return await CallAzureOpenAiAsync(sourceId, sourceType, text, ct);
        }
        catch (Exception ex)
        {
            // Degrade gracefully — deterministic signals still surface from the analyzer.
            logger.LogWarning(ex,
                "Accessorial AI extraction failed for source {SourceType}/{SourceId}; " +
                "falling back to deterministic signals only.", sourceType, sourceId);
            return [];
        }
    }

    private async Task<IReadOnlyList<AccessorialSignal>> CallAzureOpenAiAsync(
        string sourceId, string sourceType, string text, CancellationToken ct)
    {
        var url = $"{_ai.Endpoint!.TrimEnd('/')}/openai/deployments/{_ai.DeploymentName}/chat/completions?api-version=2024-02-01";

        var systemPrompt =
            "You are a freight-billing signal extractor. " +
            "Given a dispatcher note or document name from a TMS, " +
            "identify any evidence of unbilled accessorials: Detention, Layover, Lumper, or Reconsignment. " +
            "Extract only evidence that is literally present in the text. Do not invent, infer, or price. " +
            "Respond with a JSON array of objects: " +
            "{ \"type\": \"Detention|Layover|Lumper|Reconsignment|Other\", \"evidenceQuote\": \"<verbatim snippet>\" }. " +
            "Return an empty array [] when no accessorial evidence is found.";

        var requestBody = new
        {
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = text },
            },
            temperature = 0,
            max_tokens = 512,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody),
        };
        request.Headers.Add("api-key", _ai.ApiKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Azure OpenAI accessorial extraction returned {StatusCode} for source {SourceType}/{SourceId}.",
                (int)response.StatusCode, sourceType, sourceId);
            return [];
        }

        var completion = await response.Content.ReadFromJsonAsync<AzureOpenAiChatResponse>(JsonOptions, ct);
        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content)) return [];

        var items = ParseSignalItems(content);
        return items.Select(item => new AccessorialSignal
        {
            Type = item.Type,
            EvidenceQuote = item.EvidenceQuote,
            SourceId = sourceId,
            SourceType = sourceType,
            Confidence = 0.85, // AI-derived signals carry slightly lower confidence than keyword matches.
        }).ToList();
    }

    private static IReadOnlyList<SignalItem> ParseSignalItems(string content)
    {
        try
        {
            // Strip markdown code fences if the model wrapped in ```json ... ```.
            var json = content.Trim();
            if (json.StartsWith("```")) json = json[(json.IndexOf('\n') + 1)..];
            if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];
            json = json.Trim();

            return JsonSerializer.Deserialize<List<SignalItem>>(json, JsonOptions) ?? [];
        }
        catch
        {
            // Any parse failure degrades to empty.
            return [];
        }
    }

    // Minimal wire shapes for the Azure OpenAI chat completions response.
    private sealed class AzureOpenAiChatResponse
    {
        public List<AzureOpenAiChoice>? Choices { get; set; }
    }

    private sealed class AzureOpenAiChoice
    {
        public AzureOpenAiMessage? Message { get; set; }
    }

    private sealed class AzureOpenAiMessage
    {
        public string? Content { get; set; }
    }

    private sealed class SignalItem
    {
        [JsonPropertyName("type")]
        public AccessorialSignalType Type { get; set; }

        [JsonPropertyName("evidenceQuote")]
        public string EvidenceQuote { get; set; } = string.Empty;
    }
}
