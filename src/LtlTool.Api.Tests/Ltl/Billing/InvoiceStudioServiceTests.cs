using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using LtlTool.Api.Features.Ltl.Billing;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Billing;

/// <summary>
/// Unit tests for the Invoice Studio service: assembly math (totals + combined driver-RPM), the
/// sibling/BOL tracking flag, draft/final locking + edit history, and the gated Alvys write previews
/// (every payload resolves to AuditOnly / NotPerformed — nothing is ever executed against Alvys).
/// </summary>
public sealed class InvoiceStudioServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    /// <summary>In-memory <see cref="IInvoiceStore"/> so the tests never touch a database.</summary>
    private sealed class InMemoryInvoiceStore : IInvoiceStore
    {
        private readonly Dictionary<string, InvoiceRecord> _rows = [];

        public void Add(InvoiceRecord record) => _rows[record.Id] = record;
        public void Update(InvoiceRecord record) => _rows[record.Id] = record;
        public InvoiceRecord? Get(string id) => _rows.TryGetValue(id, out var r) ? r : null;

        public IReadOnlyList<InvoiceRecord> List(string? parentLoadId, InvoiceStatus? status, int max)
        {
            IEnumerable<InvoiceRecord> q = _rows.Values;
            if (!string.IsNullOrWhiteSpace(parentLoadId))
                q = q.Where(r => r.ParentLoadId == parentLoadId);
            if (status is { } s)
                q = q.Where(r => r.Status == s);
            return q.OrderByDescending(r => r.UpdatedAt).Take(Math.Clamp(max, 1, 200)).ToArray();
        }
    }

    private static AlvysWriteGateway DisabledGateway()
    {
        var write = Microsoft.Extensions.Options.Options.Create(new AlvysWriteOptions
        {
            Mode = AlvysWritebackMode.Disabled,
        });
        var alvys = Microsoft.Extensions.Options.Options.Create(new AlvysOptions());
        return new AlvysWriteGateway(write, alvys);
    }

    private static InvoiceStudioService NewService(IInvoiceStore? store = null) =>
        new(store ?? new InMemoryInvoiceStore(), DisabledGateway(), new InvoicePdfBuilder(),
            new FixedTimeProvider(Now));

    private static InvoiceLoadInput Load(
        string number, bool parent, bool bol, decimal? miles = null, decimal? driverRate = null,
        params (InvoiceChargeType Type, decimal Amount)[] charges) =>
        new()
        {
            LoadNumber = number,
            IsParent = parent,
            CustomerName = "Acme Freight",
            BolPresent = bol,
            LoadedMiles = miles,
            DriverTripRate = driverRate,
            Charges = charges.Select(c => new InvoiceChargeInput { Type = c.Type, Amount = c.Amount }).ToList(),
        };

    private static AssembleInvoiceRequest TwoLoadRequest() => new()
    {
        Loads =
        [
            Load("100482", parent: true, bol: true, miles: 500m, driverRate: 900m,
                (InvoiceChargeType.Linehaul, 2000m), (InvoiceChargeType.FuelSurcharge, 300m)),
            Load("100483", parent: false, bol: false, miles: null, driverRate: 300m,
                (InvoiceChargeType.Accessorial, 400m)),
        ],
    };

    [Fact]
    public void Assemble_sums_charges_into_totals()
    {
        var view = NewService().Assemble(TwoLoadRequest(), "ops@vt.com");

        Assert.Equal(2700m, view.InvoiceTotal); // 2000 + 300 + 400
        Assert.Equal(2700m, view.CombinedRevenue);
        Assert.Equal(2300m, view.Loads.First(l => l.IsParent).LineTotal);
        Assert.Equal(400m, view.Loads.First(l => !l.IsParent).LineTotal);
    }

    [Fact]
    public void Assemble_computes_combined_driver_rpm_from_trip_rates_over_parent_miles()
    {
        var view = NewService().Assemble(TwoLoadRequest(), "ops@vt.com");

        Assert.Equal(1200m, view.CombinedDriverTripValue); // 900 + 300
        Assert.Equal(500m, view.DriverLoadedMiles);
        Assert.Equal(2.40m, view.CombinedRevenuePerMile); // 1200 / 500
    }

    [Fact]
    public void Assemble_leaves_rpm_null_when_parent_miles_unknown()
    {
        var req = new AssembleInvoiceRequest
        {
            Loads = [Load("100482", parent: true, bol: true, driverRate: 900m, charges: (InvoiceChargeType.Linehaul, 2000m))],
        };

        var view = NewService().Assemble(req, "ops@vt.com");

        Assert.Null(view.DriverLoadedMiles);
        Assert.Null(view.CombinedRevenuePerMile); // never guessed
    }

    [Fact]
    public void Assemble_tracks_siblings_missing_a_bol()
    {
        var view = NewService().Assemble(TwoLoadRequest(), "ops@vt.com");

        Assert.Equal(1, view.Loads.Count(l => !l.BolPresent));
        Assert.Equal(["100483"], view.LoadsMissingBol);
    }

    [Fact]
    public void Assemble_derives_invoice_number_from_parent_load_when_blank()
    {
        var view = NewService().Assemble(TwoLoadRequest(), "ops@vt.com");
        Assert.Equal("INV-100482", view.InvoiceNumber);
    }

    [Fact]
    public void Assemble_starts_as_draft_with_a_created_history_entry_and_no_writeback()
    {
        var view = NewService().Assemble(TwoLoadRequest(), "ops@vt.com");

        Assert.Equal(InvoiceStatus.Draft, view.Status);
        Assert.Equal("NotPerformed", view.AlvysWriteback);
        Assert.Contains(view.EditHistory, e => e.Action == "Created");
    }

    [Fact]
    public void Update_edits_a_draft_and_appends_history()
    {
        var svc = NewService();
        var created = svc.Assemble(TwoLoadRequest(), "ops@vt.com");

        var updated = svc.Update(created.Id, new UpdateInvoiceRequest
        {
            Notes = "revised",
            Loads = [Load("100482", parent: true, bol: true, miles: 500m, charges: (InvoiceChargeType.Linehaul, 2500m))],
        }, "biller@vt.com");

        Assert.NotNull(updated);
        Assert.Equal(2500m, updated!.InvoiceTotal);
        Assert.Equal("revised", updated.Notes);
        Assert.Contains(updated.EditHistory, e => e.Action == "Edited");
    }

    [Fact]
    public void Update_on_a_final_invoice_throws_locked()
    {
        var svc = NewService();
        var created = svc.Assemble(TwoLoadRequest(), "ops@vt.com");
        svc.Finalize(created.Id, "ops@vt.com");

        Assert.Throws<InvoiceLockedException>(() =>
            svc.Update(created.Id, new UpdateInvoiceRequest { Loads = TwoLoadRequest().Loads }, "ops@vt.com"));
    }

    [Fact]
    public void Finalize_locks_and_is_idempotent()
    {
        var svc = NewService();
        var created = svc.Assemble(TwoLoadRequest(), "ops@vt.com");

        var first = svc.Finalize(created.Id, "ops@vt.com");
        var second = svc.Finalize(created.Id, "ops@vt.com");

        Assert.Equal(InvoiceStatus.Final, first!.Status);
        Assert.NotNull(first.FinalizedAt);
        Assert.Equal(InvoiceStatus.Final, second!.Status);
        Assert.Contains(first.EditHistory, e => e.Action == "Finalized");
    }

    [Fact]
    public void List_filters_by_status_and_orders_newest_first()
    {
        var store = new InMemoryInvoiceStore();
        var svc = NewService(store);
        var a = svc.Assemble(TwoLoadRequest(), "ops@vt.com");
        svc.Assemble(TwoLoadRequest(), "ops@vt.com");
        svc.Finalize(a.Id, "ops@vt.com");

        var finals = svc.List(parentLoadId: null, status: InvoiceStatus.Final, max: 50);
        var drafts = svc.List(parentLoadId: null, status: InvoiceStatus.Draft, max: 50);

        Assert.Single(finals);
        Assert.Equal(a.Id, finals[0].Id);
        Assert.Single(drafts);
    }

    [Fact]
    public void Get_and_BuildPdf_return_null_for_an_unknown_invoice()
    {
        var svc = NewService();
        Assert.Null(svc.Get("missing"));
        Assert.Null(svc.BuildPdf("missing"));
    }

    [Fact]
    public void BuildPdf_returns_a_pdf_for_a_known_invoice()
    {
        var svc = NewService();
        var view = svc.Assemble(TwoLoadRequest(), "ops@vt.com");

        var bytes = svc.BuildPdf(view.Id);

        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes!);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes!, 0, 4));
    }

    // ---- Gated Alvys previews --------------------------------------------------------------------

    [Fact]
    public void AlvysPreviews_are_all_gated_and_never_executed()
    {
        var svc = NewService();
        var view = svc.Assemble(TwoLoadRequest(), "ops@vt.com");

        var previews = svc.BuildAlvysPreviews(view.Id, parentLoadEtag: null);

        Assert.NotNull(previews);
        Assert.Equal(3, previews!.Count);
        Assert.All(previews, p => Assert.False(p.Executed));
        Assert.All(previews, p => Assert.NotEqual(AlvysOperationDisposition.SandboxExecuted, p.Disposition));
        Assert.All(previews, p => Assert.NotEqual(AlvysOperationDisposition.InternalExecuted, p.Disposition));
    }

    [Fact]
    public void AlvysPreviews_returns_null_for_an_unknown_invoice()
    {
        Assert.Null(NewService().BuildAlvysPreviews("missing", parentLoadEtag: null));
    }

    [Fact]
    public void Load_update_preview_blocks_without_an_etag()
    {
        var svc = NewService();
        var view = svc.Assemble(TwoLoadRequest(), "ops@vt.com");

        var loadUpdate = svc.BuildAlvysPreviews(view.Id, parentLoadEtag: null)!
            .Single(p => p.OperationCode == "load-update");

        Assert.Equal(AlvysOperationDisposition.Blocked, loadUpdate.Disposition);
        Assert.Contains(loadUpdate.Validation, i => i.Code == "ETAG_REQUIRED");
        Assert.Null(loadUpdate.Payload);
    }

    [Fact]
    public void Load_update_preview_carries_invoice_number_as_order_number_with_an_etag()
    {
        var svc = NewService();
        var view = svc.Assemble(TwoLoadRequest(), "ops@vt.com");

        var loadUpdate = svc.BuildAlvysPreviews(view.Id, parentLoadEtag: "v7")!
            .Single(p => p.OperationCode == "load-update");

        Assert.Equal(AlvysOperationDisposition.AuditOnly, loadUpdate.Disposition);
        Assert.NotNull(loadUpdate.Payload);
        Assert.Equal(view.InvoiceNumber, loadUpdate.Payload!.Body["OrderNumber"]);
    }

    [Fact]
    public void Customer_payment_preview_posts_the_invoice_total_keyed_by_invoice_number()
    {
        var svc = NewService();
        var view = svc.Assemble(TwoLoadRequest(), "ops@vt.com");

        var payment = svc.BuildAlvysPreviews(view.Id, parentLoadEtag: null)!
            .Single(p => p.OperationCode == "create-customer-payment");

        Assert.Equal(AlvysOperationDisposition.AuditOnly, payment.Disposition);
        Assert.NotNull(payment.Payload);
        // Alvys money shape: Amount{Amount,Currency} (see alvys_write_surface.md).
        var amount = Assert.IsType<Dictionary<string, object?>>(payment.Payload!.Body["Amount"]);
        Assert.Equal(view.InvoiceTotal, amount["Amount"]);
        Assert.Equal("USD", amount["Currency"]);
        Assert.Equal(view.InvoiceNumber, payment.Payload.Body["ReferenceNumber"]);
    }
}
