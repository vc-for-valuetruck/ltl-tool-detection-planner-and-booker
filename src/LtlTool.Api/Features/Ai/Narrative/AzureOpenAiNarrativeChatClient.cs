using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace LtlTool.Api.Features.Ai.Narrative;

/// <summary>
/// Azure OpenAI-backed <see cref="INarrativeChatClient"/>. The only file that references the
/// <c>Azure.AI.OpenAI</c> SDK, so the rest of the narrative slice stays SDK-agnostic and testable.
///
/// <para>
/// Guardrails: authentication is <c>DefaultAzureCredential</c> (managed identity in Azure, developer
/// credentials locally) — there is no API key anywhere, so no secret can leak into config, source,
/// tests, or a screenshot. The client is built lazily on first use so a disabled feature (the
/// default) never constructs a credential or opens a connection. Any failure throws to the caller,
/// which fail-closes to a null narrative.
/// </para>
/// </summary>
public sealed class AzureOpenAiNarrativeChatClient(IOptions<AzureOpenAiOptions> options)
    : INarrativeChatClient
{
    private readonly AzureOpenAiOptions _options = options.Value;
    private readonly object _gate = new();
    private ChatClient? _chatClient;

    public async Task<string?> CompleteJsonAsync(
        string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var chat = GetChatClient();

        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt),
        };

        var completionOptions = new ChatCompletionOptions
        {
            Temperature = 0f,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        ChatCompletion completion = await chat.CompleteChatAsync(messages, completionOptions, ct);

        var parts = completion.Content;
        if (parts is null || parts.Count == 0) return null;
        return parts[0].Text;
    }

    private ChatClient GetChatClient()
    {
        if (_chatClient is not null) return _chatClient;
        lock (_gate)
        {
            if (_chatClient is not null) return _chatClient;

            if (!_options.IsConfigured)
            {
                // Fail closed: an enabled-but-unconfigured feature must not silently no-op with a
                // fabricated narrative. Throwing here is caught by the service and surfaces as null.
                throw new InvalidOperationException(
                    "Azure OpenAI is not configured (AI:AzureOpenAI:Endpoint / Deployment are required).");
            }

            var client = new AzureOpenAIClient(new Uri(_options.Endpoint), new DefaultAzureCredential());
            _chatClient = client.GetChatClient(_options.Deployment);
            return _chatClient;
        }
    }
}
