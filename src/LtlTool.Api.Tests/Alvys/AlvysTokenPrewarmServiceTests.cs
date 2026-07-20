using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies the startup token pre-warm (issue #80): it acquires a token when the Live provider is
/// configured with credentials, no-ops for Fallback / missing credentials, and never lets a token
/// failure surface out of <see cref="AlvysTokenPrewarmService.StartAsync"/> (startup must not break).
/// </summary>
public sealed class AlvysTokenPrewarmServiceTests
{
    private sealed class RecordingTokenProvider(bool throws = false) : IAlvysTokenProvider
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Called => _called.Task;
        public int CallCount { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            CallCount++;
            _called.TrySetResult();
            if (throws) throw new InvalidOperationException("token endpoint down");
            return Task.FromResult("prewarmed-token");
        }
    }

    private static AlvysTokenPrewarmService Build(RecordingTokenProvider provider, AlvysOptions options) =>
        new(provider, Options.Create(options), NullLogger<AlvysTokenPrewarmService>.Instance);

    [Fact]
    public async Task Live_with_credentials_prewarms_token()
    {
        var provider = new RecordingTokenProvider();
        var svc = Build(provider, new AlvysOptions
        {
            Provider = AlvysProvider.Live,
            ClientId = "id",
            ClientSecret = "secret",
        });

        await svc.StartAsync(CancellationToken.None);
        await provider.Called.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Fallback_provider_does_not_prewarm()
    {
        var provider = new RecordingTokenProvider();
        var svc = Build(provider, new AlvysOptions { Provider = AlvysProvider.Fallback });

        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Live_without_credentials_does_not_prewarm()
    {
        var provider = new RecordingTokenProvider();
        var svc = Build(provider, new AlvysOptions { Provider = AlvysProvider.Live });

        await svc.StartAsync(CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Token_failure_does_not_break_startup()
    {
        var provider = new RecordingTokenProvider(throws: true);
        var svc = Build(provider, new AlvysOptions
        {
            Provider = AlvysProvider.Live,
            ClientId = "id",
            ClientSecret = "secret",
        });

        // StartAsync must complete without throwing even though the token endpoint fails.
        await svc.StartAsync(CancellationToken.None);
        await provider.Called.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, provider.CallCount);
    }
}
