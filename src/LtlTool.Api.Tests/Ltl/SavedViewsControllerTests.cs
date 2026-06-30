using System.Security.Claims;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.SavedViews;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the saved-views controller: presets are always returned, user-view CRUD is owner-scoped,
/// validation rejects empty/oversized names, and unknown/foreign ids 404. The controller takes no
/// Alvys dependency at all, so persisting a view cannot reach the upstream source of truth.
/// </summary>
public sealed class SavedViewsControllerTests
{
    private static SavedViewsController Build(
        ISavedViewStore store, string? user = "dispatcher@valuetruck.com")
    {
        var controller = new SavedViewsController(store);
        var identity = user is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim("preferred_username", user)], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private static SavedViewsController Build(string? user = "dispatcher@valuetruck.com") =>
        Build(new InMemorySavedViewStore(LtlTestFactory.Clock()), user);

    private static SavedViewRequest Req(string? name) =>
        new() { Name = name, Filters = new SavedViewFilters { Stage = WorkflowStage.Match } };

    [Fact]
    public void List_returns_built_in_presets_and_no_user_views_initially()
    {
        var body = Assert.IsType<SavedViewCollection>(
            Assert.IsType<OkObjectResult>(Build().List().Result).Value);

        Assert.NotEmpty(body.Presets);
        Assert.All(body.Presets, p => Assert.True(p.IsBuiltIn));
        Assert.Contains(body.Presets, p => p.Id == "preset-available-ltl");
        Assert.Contains(body.Presets, p => p.Id == "preset-ready-to-bill");
        Assert.Empty(body.Views);
    }

    [Fact]
    public void Presets_only_use_supported_filters_and_never_fabricate()
    {
        // Guard against a preset quietly being given a value the search grid cannot honestly serve.
        foreach (var preset in SavedViewPresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            Assert.Null(preset.OwnerId);
        }
    }

    [Fact]
    public void Create_persists_a_user_view_and_returns_201()
    {
        var store = new InMemorySavedViewStore(LtlTestFactory.Clock());
        var controller = Build(store);

        var created = Assert.IsType<CreatedAtActionResult>(controller.Create(Req("Hot lanes")).Result);
        var view = Assert.IsType<SavedView>(created.Value);

        Assert.Equal("Hot lanes", view.Name);
        Assert.False(view.IsBuiltIn);
        Assert.Single(store.ListForOwner("dispatcher@valuetruck.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_name_with_400(string? name)
    {
        var result = Build().Create(Req(name));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void Create_rejects_oversized_name_with_400()
    {
        var result = Build().Create(Req(new string('x', 81)));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void Update_changes_an_owned_view()
    {
        var store = new InMemorySavedViewStore(LtlTestFactory.Clock());
        var controller = Build(store);
        var created = (SavedView)((CreatedAtActionResult)controller.Create(Req("Original")).Result!).Value!;

        var result = controller.Update(created.Id, Req("Renamed"));
        var updated = Assert.IsType<SavedView>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("Renamed", updated.Name);
    }

    [Fact]
    public void Update_unknown_id_returns_404()
    {
        Assert.IsType<NotFoundResult>(Build().Update("nope", Req("x")).Result);
    }

    [Fact]
    public void Update_built_in_preset_id_returns_404()
    {
        // Presets are never stored, so attempting to update one is a not-found by construction.
        Assert.IsType<NotFoundResult>(Build().Update("preset-ready-to-bill", Req("x")).Result);
    }

    [Fact]
    public void Delete_owned_view_returns_204_then_404()
    {
        var store = new InMemorySavedViewStore(LtlTestFactory.Clock());
        var controller = Build(store);
        var created = (SavedView)((CreatedAtActionResult)controller.Create(Req("Temp")).Result!).Value!;

        Assert.IsType<NoContentResult>(controller.Delete(created.Id));
        Assert.IsType<NotFoundResult>(controller.Delete(created.Id));
    }

    [Fact]
    public void Views_are_not_visible_across_dispatchers()
    {
        var store = new InMemorySavedViewStore(LtlTestFactory.Clock());
        Build(store, "alice@valuetruck.com").Create(Req("Alice only"));

        var bobBody = Assert.IsType<SavedViewCollection>(
            Assert.IsType<OkObjectResult>(Build(store, "bob@valuetruck.com").List().Result).Value);

        Assert.Empty(bobBody.Views);
    }
}
