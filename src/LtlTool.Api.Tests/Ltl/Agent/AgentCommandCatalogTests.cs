using LtlTool.Api.Features.Ltl.Agent;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Agent;

/// <summary>Tests the tool-style command catalog used for schema discovery.</summary>
public sealed class AgentCommandCatalogTests
{
    [Fact]
    public void Catalog_lists_the_six_m4_commands()
    {
        var names = AgentCommandCatalog.All.Select(s => s.Name).ToArray();
        Assert.Equal(6, names.Length);
        Assert.Contains(AgentCommandCatalog.ListOpportunities, names);
        Assert.Contains(AgentCommandCatalog.ExplainPlan, names);
        Assert.Contains(AgentCommandCatalog.CheckFit, names);
        Assert.Contains(AgentCommandCatalog.SequenceStops, names);
        Assert.Contains(AgentCommandCatalog.EstimateQuote, names);
        Assert.Contains(AgentCommandCatalog.ReportIncident, names);
    }

    [Fact]
    public void Every_command_is_read_only()
    {
        Assert.All(AgentCommandCatalog.All, s => Assert.True(s.ReadOnly));
    }

    [Fact]
    public void Every_command_has_a_description_and_named_parameters()
    {
        Assert.All(AgentCommandCatalog.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.All(s.Parameters, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        });
    }

    [Fact]
    public void Get_is_case_insensitive_and_returns_null_for_unknown()
    {
        Assert.NotNull(AgentCommandCatalog.Get("ESTIMATE-QUOTE"));
        Assert.NotNull(AgentCommandCatalog.Get("estimate-quote"));
        Assert.Null(AgentCommandCatalog.Get("no-such-command"));
    }
}
