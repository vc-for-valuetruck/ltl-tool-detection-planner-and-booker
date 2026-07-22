namespace LtlTool.Api.Features.Ai.Narrative;

/// <summary>
/// Thin seam over the chat-completions call. Keeps the Azure OpenAI SDK isolated to a single
/// implementation so <see cref="NarrativeService"/> can be unit-tested with a hand-rolled fake and
/// so a model/SDK swap never touches the service logic. The contract is deliberately raw — it
/// returns the model's text content (expected to be a JSON object) and does no parsing itself.
/// </summary>
public interface INarrativeChatClient
{
    /// <summary>
    /// Sends the system + user prompt and returns the assistant's raw text content, or <c>null</c>
    /// when the model produced no content. Implementations may throw on transport/credential
    /// failures; the caller fail-closes on any exception.
    /// </summary>
    Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}
