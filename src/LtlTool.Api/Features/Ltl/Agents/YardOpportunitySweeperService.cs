using LtlTool.Api.Features.Integrations.Yard.Webhooks;
using LtlTool.Api.Features.Ltl.Notifications;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Agents;

/// <summary>
/// Background agent (default every 60s) that sweeps yard-originated LTL consolidation drafts —
/// persisted from the Yard webhook boundary (issue #166, <c>LtlDraftCreated</c> events) — and raises
/// one in-app <see cref="NotificationStage.OpportunityDetected"/> notification per draft. Dedupe is
/// per opportunity id, for the lifetime of the store — one "a yard draft needs review" ping per draft.
///
/// <para>
/// Closes the automation gap between a dock worker/Yard system creating a consolidation draft and a
/// dispatcher or load planner noticing it — today that draft is only visible by opening the Dock
/// screen's incoming-opportunity list. Reuses <see cref="IYardWebhookStore.ListOpportunities"/> (the
/// exact same read the Dock screen's <c>GET /api/ltl/dock/opportunities</c> already serves) and the
/// shared <see cref="YardOpportunityView"/> projection; nothing new is derived or invented.
/// </para>
///
/// <para>
/// Posture: Yard is a peer system (docs/BOUNDARIES.md). This agent reads the LTL tool's own durable
/// Yard-webhook store only — it never calls Alvys and never calls Yard directly, so it carries no
/// Alvys-availability probe (unlike the other sweepers).
/// </para>
/// </summary>
public sealed class YardOpportunitySweeperService(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<AgentsOptions> options,
    ILogger<YardOpportunitySweeperService> logger)
    : AgentBackgroundService(services, clock, logger)
{
    public const string Name = "yard-opportunity-sweeper";

    private const int ScanLimit = 50;

    private readonly YardOpportunitySweeperOptions _options = options.Value.YardOpportunitySweeper;

    public override string AgentName => Name;
    public override bool Enabled => _options.Enabled;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(_options.IntervalSeconds);

    protected override async Task<AgentSweepResult> SweepAsync(IServiceProvider scope, CancellationToken ct)
    {
        var store = scope.GetRequiredService<IYardWebhookStore>();
        var dispatcher = scope.GetRequiredService<NotificationDispatcher>();

        var opportunities = store.ListOpportunities(ScanLimit);
        foreach (var opportunity in opportunities)
        {
            ct.ThrowIfCancellationRequested();
            await dispatcher.DispatchAsync(ToTrigger(opportunity), ct);
        }

        return AgentSweepResult.Healthy(opportunities.Count);
    }

    /// <summary>
    /// Maps a yard-originated opportunity to an <see cref="NotificationStage.OpportunityDetected"/>
    /// trigger — the same stage the Alvys-derived <see cref="OpportunitySweeperService"/> uses, since
    /// both describe "a consolidation opportunity needs a look," just from different sources.
    /// <c>SourceKey</c> is the opportunity id (already the store's idempotency anchor on the source
    /// webhook event id) and <c>OccurredAt</c> is when the LTL tool received the draft, so the
    /// dispatcher idempotency key is one-per-draft-forever. Exposed for unit testing.
    /// </summary>
    public static NotificationTrigger ToTrigger(YardLtlOpportunity opportunity)
    {
        var view = YardOpportunityView.From(opportunity);
        var yard = string.IsNullOrWhiteSpace(view.YardCode) ? "the yard" : view.YardCode;
        var siblingCount = view.SiblingLoadIds.Count;
        var parent = view.ParentLoadId;

        var summary = parent is null
            ? $"Yard draft {view.DraftId} from {yard}: {siblingCount} sibling load(s) ready to review in Dock."
            : $"Yard draft {view.DraftId} from {yard} for load {parent}: {siblingCount} sibling load(s) ready to review in Dock.";

        return new NotificationTrigger
        {
            Stage = NotificationStage.OpportunityDetected,
            SourceKey = $"yard-opp:{view.Id}",
            Title = $"Yard opportunity · {view.DraftId}",
            Summary = summary,
            LoadId = view.ParentLoadId,
            LinkPath = "/ltl/dock",
            OccurredAt = view.ReceivedAt,
        };
    }
}
