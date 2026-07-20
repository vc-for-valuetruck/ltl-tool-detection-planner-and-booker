using System.Security.Claims;
using System.Text.Json;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.YardArtifacts;

/// <summary>
/// Yard-artifact intake boundary (Phase 8.2). Dock workers submit a completed Level-1 inspection
/// (structured JSON) plus photos and an optional generated PDF; the artifact is keyed by truck unit /
/// trailer unit / load number + yard + timestamp + submitted-by and stored internally (SQL metadata +
/// file store). The arrivals board and load-detail page read these back through <see cref="Query"/>.
///
/// <para>This is <b>our</b> data, not Alvys. Nothing here reads from or writes to Alvys — the read-only
/// posture is unchanged.</para>
/// </summary>
[ApiController]
[Route("api/ltl/yard-artifacts")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
public sealed class YardArtifactsController(YardArtifactService service) : ControllerBase
{
    /// <summary>Accepts a multipart submission: a JSON <c>payload</c> field + <c>photos</c> + optional <c>pdf</c>.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(YardArtifactView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit([FromForm] YardArtifactForm form, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(form.Payload))
        {
            return BadRequest(new { error = "The 'payload' form field (inspection JSON) is required." });
        }

        YardArtifactSubmission? submission;
        try
        {
            submission = JsonSerializer.Deserialize<YardArtifactSubmission>(
                form.Payload, YardArtifactMapping.Json);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "The 'payload' form field is not valid JSON." });
        }

        if (submission is null)
        {
            return BadRequest(new { error = "The 'payload' form field is empty." });
        }

        var uploads = new List<YardArtifactUpload>();
        foreach (var photo in form.Photos ?? [])
        {
            uploads.Add(new YardArtifactUpload(
                YardArtifactFileKind.Photo, photo.FileName, photo.ContentType, photo.OpenReadStream()));
        }
        if (form.Pdf is not null)
        {
            uploads.Add(new YardArtifactUpload(
                YardArtifactFileKind.Pdf, form.Pdf.FileName, form.Pdf.ContentType, form.Pdf.OpenReadStream()));
        }

        try
        {
            var view = await service.CreateAsync(submission, uploads, CurrentUser(), ct);
            return CreatedAtAction(nameof(GetById), new { id = view.Id }, view);
        }
        catch (YardArtifactValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Artifacts for surfacing, filtered by any of load number / truck unit / trailer unit / yard.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<YardArtifactView>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<YardArtifactView>> Query(
        [FromQuery] string? loadNumber,
        [FromQuery] string? truckUnit,
        [FromQuery] string? trailerUnit,
        [FromQuery] string? yard,
        [FromQuery] int max = 100)
        => Ok(service.Query(new YardArtifactQuery(loadNumber, truckUnit, trailerUnit, yard, max)));

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(YardArtifactView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<YardArtifactView> GetById(string id)
    {
        var view = service.Get(id);
        return view is null ? NotFound() : Ok(view);
    }

    /// <summary>Streams a stored photo/PDF for the inline gallery / PDF download.</summary>
    [HttpGet("{id}/files/{fileId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetFile(string id, string fileId)
    {
        var file = service.OpenFile(id, fileId);
        return file is null ? NotFound() : File(file.Content, file.ContentType, file.FileName);
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}

/// <summary>Multipart form model for a yard-artifact submission.</summary>
public sealed class YardArtifactForm
{
    /// <summary>The structured inspection JSON (a serialized <see cref="YardArtifactSubmission"/>).</summary>
    public string? Payload { get; set; }

    public List<IFormFile>? Photos { get; set; }

    public IFormFile? Pdf { get; set; }
}
