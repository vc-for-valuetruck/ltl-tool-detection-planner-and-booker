using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Arrivals;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.DispatchPlanner;
using LtlTool.Api.Features.Ltl.Dock;
using LtlTool.Api.Features.Ltl.Notifications;
using LtlTool.Api.Features.Ltl.Optimization;
using LtlTool.Api.Tests.Ltl.Consolidation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Dock;

/// <summary>
/// Behavior tests for Dock mode (Phase 2.5). Dock mode is a thin orchestration over the
/// already-tested arrivals + consolidation services, so these tests focus on the seams the
/// orchestrator adds: warehouse projection, warehouse-scoped arrivals, and a combine that records
/// an internal audit (never an Alvys write).
/// </summary>
public sealed class DockServiceTests
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(LtlTestFactory.Now.UtcDateTime);
    private static readonly DateTimeOffset ParentPickup = new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);

    private static DockService Build(
        FakeAlvysClient client,
        ConsolidationOptions? overrides = null,
        DockOptions? dockOptions = null,
        InMemoryConsolidationAuditStore? auditStore = null)
    {
        var opts = overrides ?? new ConsolidationOptions();
        var optsWrap = Microsoft.Extensions.Options.Options.Create(opts);

        var loads = new LtlLoadService(
            client, LtlTestFactory.Normalizer(), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options(), LtlTestFactory.Clock());

        var arrivals = new LaredoArrivalsService(
            client, LtlTestFactory.Options(), optsWrap, LtlTestFactory.Clock());

        var candidates = new ConsolidationCandidateService(
            loads, optsWrap, LtlTestFactory.Options(), LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(opts));

        var plans = new ConsolidationPlanService(
            loads, optsWrap, LtlTestFactory.Options(), LtlTestFactory.Clock(),
            LtlTestFactory.StaticPolicyReader(opts),
            new NullTrailerFitService(TimeProvider.System),
            new NullStopSequencer(LtlTestFactory.Clock()));

        var audits = auditStore ?? new InMemoryConsolidationAuditStore(LtlTestFactory.Clock());

        var notifications = new DockNotificationService(
            [new InAppNotificationChannel(), new EmailNotificationChannel(NotificationOptionsWrap())],
            new InMemoryNotificationStore(),
            Microsoft.Extensions.Options.Options.Create(dockOptions ?? new DockOptions()),
            LtlTestFactory.Clock(),
            NullLogger<DockNotificationService>.Instance);

        var dispatchPlanner = new DispatchPlannerService(
            client, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DispatchPlannerService>.Instance);

        return new DockService(arrivals, candidates, plans, audits, notifications, dispatchPlanner, optsWrap);
    }

    private static Microsoft.Extensions.Options.IOptions<NotificationOptions> NotificationOptionsWrap()
        => Microsoft.Extensions.Options.Options.Create(new NotificationOptions());

    private static DateTimeOffset OnDay(int hour) =>
        new(Day.Year, Day.Month, Day.Day, hour, 0, 0, TimeSpan.Zero);

    private static AlvysTripStop Stop(int sequence, string stopType, string city, string state, DateTimeOffset window)
        => new()
        {
            Sequence = sequence,
            StopType = stopType,
            Address = new AlvysAddress { City = city, State = state },
            StopWindowStart = window,
        };

    private static AlvysLoad Load(
        string id, string customer, string originCity, string originState,
        string destCity, string destState, DateTimeOffset pickup,
        decimal? rate = null, decimal? mileage = null, decimal? weight = null)
        => new()
        {
            Id = id,
            LoadNumber = id,
            Status = "Available",
            CustomerName = customer,
            CustomerRate = rate,
            CustomerMileage = mileage,
            Weight = weight,
            Stops =
            [
                new AlvysLoadStop
                {
                    StopType = "Pickup",
                    Address = new AlvysAddress { City = originCity, State = originState },
                    ScheduledStart = pickup,
                    Sequence = 1,
                },
                new AlvysLoadStop
                {
                    StopType = "Delivery",
                    Address = new AlvysAddress { City = destCity, State = destState },
                    ScheduledStart = pickup.AddDays(1),
                    Sequence = 2,
                },
            ],
        };

    [Fact]
    public async Task ListWarehouses_projects_the_configured_yards()
    {
        var service = Build(new FakeAlvysClient());

        var response = await service.ListWarehousesAsync(default);

        Assert.Equal(2, response.Warehouses.Count);
        Assert.Contains(response.Warehouses, w => w.Code == "LAREDO");
        Assert.Contains(response.Warehouses, w => w.Code == "DALLAS");
    }

    [Fact]
    public async Task ListWarehouses_is_empty_when_no_warehouses_configured()
    {
        var service = Build(new FakeAlvysClient(), new ConsolidationOptions { Warehouses = [] });

        Assert.Empty((await service.ListWarehousesAsync(default)).Warehouses);
    }

    [Fact]
    public async Task ListWarehouses_enriches_from_alvys_location_when_configured_and_degrades_otherwise()
    {
        // LAREDO carries an Alvys location id and resolves → enriched type + address. DALLAS carries
        // no id → honest null enrichment (degrades to static name/state), never fabricated geography.
        var client = new FakeAlvysClient
        {
            Locations =
            [
                new AlvysLocation
                {
                    Id = "LOC-LAREDO",
                    Name = "Laredo Cross-Dock",
                    Type = "Warehouse",
                    PhysicalAddress = new AlvysContextAddress
                    {
                        Street = "1 Bridge Rd", City = "Laredo", State = "TX", ZipCode = "78045",
                    },
                },
            ],
        };
        var options = new ConsolidationOptions();
        options.Warehouses[0].AlvysLocationId = "LOC-LAREDO"; // LAREDO

        var response = await Build(client, options).ListWarehousesAsync(default);

        var laredo = Assert.Single(response.Warehouses, w => w.Code == "LAREDO");
        Assert.Equal("Warehouse", laredo.LocationType);
        Assert.Equal("1 Bridge Rd, Laredo, TX 78045", laredo.AddressLabel);

        var dallas = Assert.Single(response.Warehouses, w => w.Code == "DALLAS");
        Assert.Null(dallas.LocationType);
        Assert.Null(dallas.AddressLabel);
    }

    [Fact]
    public async Task GetArrivals_scopes_the_board_to_the_requested_warehouse()
    {
        // A trip with a Dallas-area stop on the target day should surface on the Dallas board — the
        // arrivals generalization must resolve any configured warehouse, not just the pilot origin.
        var client = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip
                {
                    Id = "P-DAL",
                    TripNumber = "T-DAL",
                    Stops =
                    [
                        Stop(1, "Pickup", "Fort Worth", "TX", OnDay(6)),
                        Stop(2, "Delivery", "Dallas", "TX", OnDay(10)),
                    ],
                },
            ],
        };

        var board = await Build(client).GetArrivalsAsync("DALLAS", null, default);

        Assert.Equal("DALLAS", board.Yard);
        var arrival = Assert.Single(board.Arrivals);
        Assert.Equal("P-DAL", arrival.TripId);
        // Dallas is only ever a corridor endpoint (no onward corridor originates there), so the
        // onward "bound" highlight is honestly false rather than fabricated.
        Assert.False(arrival.DallasBound);
    }

    [Fact]
    public async Task GetArrivals_with_unknown_warehouse_degrades_to_empty_board()
    {
        var client = new FakeAlvysClient
        {
            Trips =
            [
                new AlvysTrip { Id = "P1", Stops = [Stop(1, "Pickup", "Laredo", "TX", OnDay(8))] },
            ],
        };

        var board = await Build(client).GetArrivalsAsync("NOPE", null, default);

        Assert.Empty(board.Arrivals);
    }

    [Fact]
    public async Task Combine_builds_the_plan_and_records_an_internal_audit()
    {
        var parent = Load("L-100234", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup,
            rate: 4100m, mileage: 1072m, weight: 14200m);
        var sibling = Load("L-100241", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(3),
            rate: 4100m, mileage: 500m, weight: 4100m);

        var client = new StatefulAlvysClient(parent, sibling);
        client.Trips.Add(new AlvysTrip
        {
            Id = "T-100234",
            LoadNumber = "L-100234",
            TripValue = new AlvysMoney { Amount = 4200m, Currency = "USD" },
            LoadedMileage = new AlvysDistanceMeasurement
            {
                Distance = new AlvysDistance { Value = 1050m, UnitOfMeasure = "Miles" },
            },
        });
        client.Trips.Add(new AlvysTrip
        {
            Id = "T-100241",
            LoadNumber = "L-100241",
            TripValue = new AlvysMoney { Amount = 3900m, Currency = "USD" },
            LoadedMileage = new AlvysDistanceMeasurement
            {
                Distance = new AlvysDistance { Value = 490m, UnitOfMeasure = "Miles" },
            },
        });

        var options = new ConsolidationOptions();
        options.CustomerPolicies.Add(new() { Customer = "Verdef", Tier = CustomerConsolidationTier.Allowed });
        var service = Build(client, options);

        var response = await service.CombineAsync(
            new DockCombineRequest { ParentLoadId = "L-100234", SiblingLoadIds = ["L-100241"] },
            "dock.worker@valuetruck.com",
            default);

        Assert.Empty(response.Plan.Blockers);
        Assert.Equal("L-100234", response.Plan.Parent.LoadNumber);
        Assert.Equal("L-100241", Assert.Single(response.Plan.Siblings).LoadNumber);

        // The one state written is the internal audit — never an Alvys write.
        Assert.Equal("NotPerformed", response.Audit.AlvysWriteback);
        Assert.Equal("dock.worker@valuetruck.com", response.Audit.RecordedBy);
        Assert.Equal("L-100234", response.Audit.ParentLoadId);
        Assert.Contains("L-100241", response.Audit.SiblingLoadNumbers);
    }

    [Fact]
    public async Task Combine_defaults_the_corridor_to_the_pilot_when_omitted()
    {
        var parent = Load("L-1", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup);
        var sibling = Load("L-2", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(2));
        var client = new StatefulAlvysClient(parent, sibling);

        var response = await Build(client).CombineAsync(
            new DockCombineRequest { ParentLoadId = "L-1", SiblingLoadIds = ["L-2"], CorridorCode = null },
            "dock.worker",
            default);

        Assert.Equal("LAREDO_TO_DALLAS", response.Plan.CorridorCode);
    }

    [Fact]
    public async Task Combine_with_blocked_plan_throws_and_records_nothing()
    {
        // Parent sits OFF the Laredo→Dallas corridor (Miami→Atlanta). The plan resolves but is
        // illegal, so the plan carries a hard blocker. Phase 3 semantics: the combine must fail
        // closed — throw the blocked exception and record NO audit.
        var parent = Load("L-OFF", "Verdef", "Miami", "FL", "Atlanta", "GA", ParentPickup);
        var sibling = Load("L-2", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(2));
        var client = new StatefulAlvysClient(parent, sibling);
        var audits = new InMemoryConsolidationAuditStore(LtlTestFactory.Clock());
        var service = Build(client, auditStore: audits);

        var ex = await Assert.ThrowsAsync<ConsolidationPlanBlockedException>(
            () => service.CombineAsync(
                new DockCombineRequest { ParentLoadId = "L-OFF", SiblingLoadIds = ["L-2"] },
                "dock.worker@valuetruck.com",
                default));

        Assert.NotEmpty(ex.Plan.Blockers);
        // Nothing was recorded — the guardrail wrote no audit for a blocked plan.
        Assert.Empty(audits.All());
    }

    [Fact]
    public async Task Combine_bubbles_bad_request_for_missing_parent()
    {
        var service = Build(new FakeAlvysClient());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CombineAsync(
                new DockCombineRequest { ParentLoadId = "", SiblingLoadIds = ["L-2"] },
                "dock.worker",
                default));
    }

    [Fact]
    public async Task Combine_notification_is_disabled_when_no_recipients_configured_for_the_yard()
    {
        var parent = Load("L-1", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup);
        var sibling = Load("L-2", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(2));
        var client = new StatefulAlvysClient(parent, sibling);

        var response = await Build(client).CombineAsync(
            new DockCombineRequest { ParentLoadId = "L-1", SiblingLoadIds = ["L-2"], WarehouseCode = "LAREDO" },
            "dock.worker",
            default);

        // Empty Ltl:Dock:NotifyRecipients → honest "Disabled", not a fabricated delivery.
        Assert.Equal("Disabled", response.Notification.State);
        Assert.Empty(response.Notification.Recipients);
    }

    [Fact]
    public async Task Combine_notification_reports_not_configured_when_recipients_set_but_email_channel_off()
    {
        var parent = Load("L-1", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup);
        var sibling = Load("L-2", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(2));
        var client = new StatefulAlvysClient(parent, sibling);

        var dockOptions = new DockOptions();
        dockOptions.NotifyRecipients["LAREDO"] = ["ops@valuetruck.com"];

        var response = await Build(client, dockOptions: dockOptions).CombineAsync(
            new DockCombineRequest { ParentLoadId = "L-1", SiblingLoadIds = ["L-2"], WarehouseCode = "LAREDO" },
            "dock.worker",
            default);

        // Recipients configured, but the shared email channel has no SMTP config → honest NotConfigured.
        Assert.Equal("NotConfigured", response.Notification.State);
        Assert.Contains("ops@valuetruck.com", response.Notification.Recipients);
    }

    [Fact]
    public async Task Undo_records_a_retraction_audit_and_writes_nothing_to_alvys()
    {
        var parent = Load("L-1", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup);
        var sibling = Load("L-2", "Verdef", "Laredo", "TX", "Dallas", "TX", ParentPickup.AddHours(2));
        var client = new StatefulAlvysClient(parent, sibling);
        var service = Build(client);

        var response = await service.UndoAsync(
            new DockUndoRequest { ParentLoadId = "L-1", SiblingLoadIds = ["L-2"] },
            "dock.worker",
            default);

        Assert.Equal("Undo", response.Audit.Action);
        Assert.Equal("NotPerformed", response.Audit.AlvysWriteback);
        Assert.Equal("L-1", response.Audit.ParentLoadId);
    }
}
