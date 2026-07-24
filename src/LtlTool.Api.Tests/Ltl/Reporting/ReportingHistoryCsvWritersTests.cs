using LtlTool.Api.Features.Ltl.Reporting;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Reporting;

public sealed class ReportingHistoryCsvWritersTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AccessorialHistoryCsvWriter_writes_header_and_row_with_empty_cells_for_missing_values()
    {
        var rows = new[]
        {
            new AccessorialRecord
            {
                Id = "a1",
                ContentKey = "content-key-1",
                LoadId = "load-1",
                LoadNumber = "L-1001",
                TripId = null,
                EntityType = AccessorialEntityType.Customer,
                Type = "Detention",
                Description = null,
                Amount = 150m,
                FirstSeenAt = Now,
                LastSeenAt = Now,
            },
        };

        var csv = AccessorialHistoryCsvWriter.Write(rows);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Id,LoadId,LoadNumber,TripId,EntityType,Type,Description,Amount,FirstSeenAt,LastSeenAt", lines[0]);
        Assert.Contains("a1,load-1,L-1001,,Customer,Detention,,150", lines[1]);
    }

    [Fact]
    public void LoadAssignmentHistoryCsvWriter_writes_header_and_row_with_empty_cells_for_missing_values()
    {
        var rows = new[]
        {
            new LoadAssignmentRecord
            {
                Id = "r1",
                LoadId = "load-1",
                LoadNumber = "L-1001",
                TripId = "trip-1",
                Status = "Dispatched",
                CarrierId = "C1",
                CarrierName = "Acme",
                TruckId = "TRK1",
                TrailerId = "TRL1",
                CapturedAt = Now,
            },
        };

        var csv = LoadAssignmentHistoryCsvWriter.Write(rows);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            "Id,LoadId,LoadNumber,TripId,Status,CarrierId,CarrierName,Driver1Id,Driver1Name,Driver2Id,Driver2Name,OwnerOperatorId,OwnerOperatorName,TruckId,TrailerId,DispatcherId,DispatchedBy,CarrierAssignedAt,CapturedAt",
            lines[0]);
        Assert.StartsWith("r1,load-1,L-1001,trip-1,Dispatched,C1,Acme,,,,,,,TRK1,TRL1,,,", lines[1]);
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A2)")]
    public void AccessorialHistoryCsvWriter_neutralizes_a_formula_leading_description(string maliciousDescription)
    {
        var rows = new[]
        {
            new AccessorialRecord
            {
                Id = "a1",
                ContentKey = "content-key-1",
                LoadId = "load-1",
                EntityType = AccessorialEntityType.Customer,
                Description = maliciousDescription,
                FirstSeenAt = Now,
                LastSeenAt = Now,
            },
        };

        var csv = AccessorialHistoryCsvWriter.Write(rows);

        // The cell must be prefixed with a leading apostrophe (forces "text" in a spreadsheet) and
        // must NOT contain the raw formula-leading character at the start of the (unquoted) value.
        Assert.Contains("'" + maliciousDescription, csv);
    }
}
