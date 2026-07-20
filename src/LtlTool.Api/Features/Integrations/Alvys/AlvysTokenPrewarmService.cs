using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// First hosted service in the app (issue #80). On startup it pre-warms the Alvys OAuth
/// client-credentials token so the very first user request — typically the <c>/ltl</c> cold-start
/// consolidation sweep — does not pay the token round-trip on top of the Azure App Service cold
/// start, which is what made the landing page sit on "0 candidate pairs" for ~15 s.
///
/// <para>
/// Safety posture: this only ever asks <see cref="IAlvysTokenProvider"/> for a token that live
/// Alvys reads already use — it introduces no new data path and touches nothing but the Alvys
/// auth endpoint. It runs off the startup thread (fire-and-forget) so a slow or failing token
/// endpoint can never block Kestrel from listening, and every failure degrades to a logged
/// warning: the token is simply fetched lazily on first use as before. It no-ops entirely when
/// the provider is <see cref="AlvysProvider.Fallback"/> or credentials are absent, so local/UAT
/// and CI never attempt a live call.
/// </para>
/// </summary>
public sealed class AlvysTokenPrewarmService(
    IAlvysTokenProvider tokenProvider,
    IOptions<AlvysOptions> options,
    ILogger<AlvysTokenPrewarmService> logger) : IHostedService
{
    private readonly AlvysOptions _options = options.Value;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Provider != AlvysProvider.Live || !_options.HasCredentials)
        {
            logger.LogInformation(
                "Alvys token pre-warm skipped (Provider={Provider}, HasCredentials={HasCredentials}). " +
                "Token will be acquired lazily on first use if needed.",
                _options.Provider, _options.HasCredentials);
            return Task.CompletedTask;
        }

        // Fire-and-forget: never block application startup on the Alvys auth endpoint.
        _ = PrewarmAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task PrewarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Pre-warming Alvys OAuth access token at startup.");
            await tokenProvider.GetAccessTokenAsync(cancellationToken);
            logger.LogInformation("Alvys OAuth access token pre-warmed; first request will skip the token round-trip.");
        }
        catch (Exception ex)
        {
            // Non-fatal: the token is fetched lazily on first real use. Never log the secret.
            logger.LogWarning(
                ex,
                "Alvys OAuth token pre-warm failed at startup. The API will still serve; the token " +
                "will be acquired on the first Alvys request instead.");
        }
    }
}
