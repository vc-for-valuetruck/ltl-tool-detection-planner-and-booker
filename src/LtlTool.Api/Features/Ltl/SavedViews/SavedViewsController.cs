using System.Security.Claims;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.SavedViews;

/// <summary>
/// Dispatcher saved-view CRUD for the LTL workbench. Views capture filter/sort presets so a
/// dispatcher can return to a queue in one click. This controller is entirely tool-local: it reads
/// and writes only the saved-view store and <b>never</b> calls Alvys, so persisting a view has no
/// effect on the upstream source of truth.
/// </summary>
[ApiController]
[Route("api/ltl/saved-views")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class SavedViewsController(ISavedViewStore store) : ControllerBase
{
    private const int MaxNameLength = 80;
    private const int MaxDescriptionLength = 280;

    /// <summary>
    /// The dispatcher's saved-view collection: shared built-in presets plus the dispatcher's own
    /// views, as two distinct lists.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SavedViewCollection), StatusCodes.Status200OK)]
    public ActionResult<SavedViewCollection> List() => Ok(new SavedViewCollection
    {
        Presets = SavedViewPresets.All,
        Views = store.ListForOwner(CurrentUser()),
    });

    /// <summary>Creates a saved view owned by the current dispatcher.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SavedView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SavedView> Create([FromBody] SavedViewRequest request)
    {
        if (Validate(request) is { } error) return error;

        var view = store.Create(CurrentUser(), request);
        return CreatedAtAction(nameof(List), new { id = view.Id }, view);
    }

    /// <summary>
    /// Updates one of the current dispatcher's saved views. Returns 404 when the id is unknown or
    /// belongs to another dispatcher; built-in preset ids are never stored, so they 404 here too.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(SavedView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SavedView> Update(string id, [FromBody] SavedViewRequest request)
    {
        if (Validate(request) is { } error) return error;

        var updated = store.Update(CurrentUser(), id, request);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Deletes one of the current dispatcher's saved views. 404 when not found.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(string id) =>
        store.Delete(CurrentUser(), id) ? NoContent() : NotFound();

    /// <summary>Validates the request, returning a 400 result when invalid, otherwise null.</summary>
    private BadRequestObjectResult? Validate(SavedViewRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("A saved view requires a non-empty name.");
        if (request.Name.Trim().Length > MaxNameLength)
            return BadRequest($"Saved view name cannot exceed {MaxNameLength} characters.");
        if (request.Description is { Length: > MaxDescriptionLength })
            return BadRequest($"Saved view description cannot exceed {MaxDescriptionLength} characters.");
        return null;
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
