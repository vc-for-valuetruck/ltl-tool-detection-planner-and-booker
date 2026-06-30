using Microsoft.Extensions.Logging;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// HttpMessageHandler that returns a scripted response and records the requests
/// (including serialized body) it received — used to drive Alvys client tests
/// without a network call.
/// </summary>
internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        return responder(request, body);
    }
}

/// <summary>IHttpClientFactory that hands out clients backed by a single stub handler.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri? baseAddress = null)
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
}

/// <summary>
/// Typed logger that captures formatted messages (and exceptions) into a list so
/// tests can assert on log content — e.g. that secrets never appear.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        if (exception is not null) Messages.Add(exception.ToString());
    }

    /// <summary>All captured text joined — convenient for substring assertions.</summary>
    public string AllText => string.Join("\n", Messages);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
