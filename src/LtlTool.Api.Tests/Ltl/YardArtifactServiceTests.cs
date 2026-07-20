using System.Text;
using LtlTool.Api.Features.Ltl.YardArtifacts;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Boundary behavior of the yard-artifact intake service: keying validation, upload limits/type
/// checks, the honest inspection roll-up (Submitted / Passed / Flagged derived from the answered
/// items — never guessed), and that dock-verified pallet/dims carry the explicit "yard verification"
/// provenance so the enrichment layer can prefer them over EDI estimates. Uses in-memory store/file
/// doubles; nothing here touches Alvys.
/// </summary>
public sealed class YardArtifactServiceTests
{
    private static YardArtifactService NewService(
        out FakeStore store, YardArtifactOptions? options = null)
    {
        store = new FakeStore();
        return new YardArtifactService(
            store, new FakeFileStore(),
            Microsoft.Extensions.Options.Options.Create(options ?? new YardArtifactOptions()),
            LtlTestFactory.Clock());
    }

    private static YardArtifactUpload Photo(string name = "front.jpg", string type = "image/jpeg") =>
        new(YardArtifactFileKind.Photo, name, type, new MemoryStream(Encoding.UTF8.GetBytes("x")));

    private static YardInspectionItemInput Item(string result) =>
        new() { Ref = "r", Label = "l", Result = result };

    [Fact]
    public async Task Requires_at_least_one_equipment_or_load_key()
    {
        var service = NewService(out _);
        var submission = new YardArtifactSubmission { Yard = "laredo" };

        await Assert.ThrowsAsync<YardArtifactValidationException>(
            () => service.CreateAsync(submission, [], "dock@valuetruck.com", CancellationToken.None));
    }

    [Fact]
    public async Task Yard_is_required_and_normalized_upper_case()
    {
        var service = NewService(out _);

        await Assert.ThrowsAsync<YardArtifactValidationException>(() =>
            service.CreateAsync(new YardArtifactSubmission { TruckUnit = "T1" }, [],
                "dock@valuetruck.com", CancellationToken.None));

        var view = await service.CreateAsync(
            new YardArtifactSubmission { Yard = "laredo", TruckUnit = "T1" }, [],
            "dock@valuetruck.com", CancellationToken.None);
        Assert.Equal("LAREDO", view.Yard);
    }

    [Fact]
    public async Task Any_fail_flags_the_inspection()
    {
        var service = NewService(out _);
        var submission = new YardArtifactSubmission
        {
            Yard = "LAREDO",
            LoadNumber = "L100",
            Items = [Item("pass"), Item("fail"), Item("na")],
        };

        var view = await service.CreateAsync(submission, [], "dock@valuetruck.com", CancellationToken.None);

        Assert.Equal(YardInspectionStatus.Flagged, view.Status);
        Assert.Equal(1, view.PassedItems);
        Assert.Equal(1, view.FailedItems);
        Assert.Equal(1, view.NaItems);
    }

    [Fact]
    public async Task All_pass_or_na_marks_passed()
    {
        var service = NewService(out _);
        var submission = new YardArtifactSubmission
        {
            Yard = "LAREDO",
            LoadNumber = "L100",
            Items = [Item("pass"), Item("n/a")],
        };

        var view = await service.CreateAsync(submission, [], "dock@valuetruck.com", CancellationToken.None);

        Assert.Equal(YardInspectionStatus.Passed, view.Status);
        Assert.Equal(1, view.PassedItems);
        Assert.Equal(1, view.NaItems);
    }

    [Fact]
    public async Task No_answered_items_stays_submitted()
    {
        var service = NewService(out _);
        var submission = new YardArtifactSubmission { Yard = "LAREDO", LoadNumber = "L100" };

        var view = await service.CreateAsync(submission, [], "dock@valuetruck.com", CancellationToken.None);

        Assert.Equal(YardInspectionStatus.Submitted, view.Status);
    }

    [Fact]
    public async Task Verified_pallets_carry_yard_verification_source()
    {
        var service = NewService(out _);
        var submission = new YardArtifactSubmission
        {
            Yard = "LAREDO",
            LoadNumber = "L100",
            VerifiedPalletCount = 12,
            VerifiedDims = new YardVerifiedDimsInput { LengthInches = 48, WidthInches = 40, HeightInches = 60 },
        };

        var view = await service.CreateAsync(submission, [], "dock@valuetruck.com", CancellationToken.None);

        Assert.NotNull(view.VerifiedPallets);
        Assert.Equal(12, view.VerifiedPallets!.PalletCount);
        Assert.Equal(48, view.VerifiedPallets.LengthInches);
        Assert.Equal(YardArtifactMapping.VerifiedSource, view.VerifiedPallets.Source);
    }

    [Fact]
    public async Task Verified_pallets_null_when_not_provided()
    {
        var service = NewService(out _);
        var submission = new YardArtifactSubmission { Yard = "LAREDO", LoadNumber = "L100" };

        var view = await service.CreateAsync(submission, [], "dock@valuetruck.com", CancellationToken.None);

        Assert.Null(view.VerifiedPallets);
    }

    [Fact]
    public async Task Rejects_more_files_than_the_limit()
    {
        var service = NewService(out _, new YardArtifactOptions { MaxFiles = 1 });
        var submission = new YardArtifactSubmission { Yard = "LAREDO", LoadNumber = "L100" };

        await Assert.ThrowsAsync<YardArtifactValidationException>(() =>
            service.CreateAsync(submission, [Photo(), Photo()], "dock@valuetruck.com", CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_unsupported_content_type()
    {
        var service = NewService(out _);
        var submission = new YardArtifactSubmission { Yard = "LAREDO", LoadNumber = "L100" };
        var bad = new YardArtifactUpload(
            YardArtifactFileKind.Photo, "notes.txt", "text/plain", new MemoryStream([1]));

        await Assert.ThrowsAsync<YardArtifactValidationException>(() =>
            service.CreateAsync(submission, [bad], "dock@valuetruck.com", CancellationToken.None));
    }

    [Fact]
    public async Task Stores_files_and_records_submitter()
    {
        var service = NewService(out var store);
        var submission = new YardArtifactSubmission { Yard = "LAREDO", TruckUnit = "T1" };

        var view = await service.CreateAsync(
            submission, [Photo()], "dock@valuetruck.com", CancellationToken.None);

        Assert.Equal("dock@valuetruck.com", view.SubmittedBy);
        Assert.Single(view.Files);
        Assert.NotNull(store.Get(view.Id));
    }

    private sealed class FakeStore : IYardArtifactStore
    {
        private readonly List<YardArtifactRecord> _records = [];

        public void Add(YardArtifactRecord record) => _records.Add(record);

        public YardArtifactRecord? Get(string id) => _records.FirstOrDefault(r => r.Id == id);

        public IReadOnlyList<YardArtifactRecord> Query(YardArtifactQuery query) => _records;
    }

    private sealed class FakeFileStore : IYardArtifactFileStore
    {
        public Task<YardArtifactFile> SaveAsync(
            string artifactId, YardArtifactFileKind kind, string fileName, string contentType,
            Stream content, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("n");
            return Task.FromResult(new YardArtifactFile(
                id, kind, fileName, contentType, 1, $"{artifactId}/{id}_{fileName}"));
        }

        public YardArtifactStreamedFile? Open(YardArtifactFile file) =>
            new(new MemoryStream([1]), file.ContentType, file.FileName);
    }
}
