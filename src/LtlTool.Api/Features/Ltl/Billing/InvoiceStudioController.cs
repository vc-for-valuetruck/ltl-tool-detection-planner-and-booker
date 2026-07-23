using System.Security.Claims;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Billing;

/// <summary>
/// Invoice Studio API: assemble a customer invoice from a consolidation (parent + sibling loads),
/// edit its per-load charges, track BOL presence, generate a downloadable PDF, and preview the exact
/// contracted Alvys write payloads. Read-only against Alvys — invoices persist app-side only and
/// every Alvys payload is a gated preview (<c>AlvysWriteback = NotPerformed</c>). Nothing is pushed
/// to Alvys from this controller.
/// </summary>
[ApiController]
[Route("api/ltl/invoices")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class InvoiceStudioController(InvoiceStudioService service) : ControllerBase
{
    private readonly InvoiceStudioService _service = service;

    /// <summary>Recent invoices, newest-updated first. Optional parentLoadId / status filters.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<InvoiceSummary>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<InvoiceSummary>> List(
        [FromQuery] string? parentLoadId,
        [FromQuery] InvoiceStatus? status,
        [FromQuery] int max = 50)
        => Ok(_service.List(parentLoadId, status, max));

    /// <summary>Assemble a new draft invoice from a consolidation. 400 when no loads are supplied.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(InvoiceView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<InvoiceView> Assemble([FromBody] AssembleInvoiceRequest request)
    {
        if (request.Loads is not { Count: > 0 })
            return BadRequest(new { error = "At least one load is required to assemble an invoice." });

        var view = _service.Assemble(request, CurrentUser());
        return CreatedAtAction(nameof(Get), new { id = view.Id }, view);
    }

    /// <summary>One invoice by id. 404 when unknown.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(InvoiceView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<InvoiceView> Get(string id)
    {
        var view = _service.Get(id);
        return view is null ? NotFound() : Ok(view);
    }

    /// <summary>Update a draft invoice's loads/charges/notes. 404 unknown, 409 when already final.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(InvoiceView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<InvoiceView> Update(string id, [FromBody] UpdateInvoiceRequest request)
    {
        try
        {
            var view = _service.Update(id, request, CurrentUser());
            return view is null ? NotFound() : Ok(view);
        }
        catch (InvoiceLockedException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Lock a draft invoice (Draft → Final). Idempotent. 404 unknown.</summary>
    [HttpPost("{id}/finalize")]
    [ProducesResponseType(typeof(InvoiceView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<InvoiceView> Finalize(string id)
    {
        var view = _service.Finalize(id, CurrentUser());
        return view is null ? NotFound() : Ok(view);
    }

    /// <summary>Download the invoice PDF. 404 when unknown.</summary>
    [HttpGet("{id}/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Pdf(string id)
    {
        var bytes = _service.BuildPdf(id);
        if (bytes is null) return NotFound();
        return File(bytes, "application/pdf", $"invoice-{id}.pdf");
    }

    /// <summary>
    /// The exact contracted Alvys write payloads for this invoice, as gated previews. Every outcome
    /// reflects the configured writeback mode (Disabled → AuditOnly by default) so nothing is sent.
    /// An optional parentLoadEtag lets the load-update (OrderNumber PATCH) preview show its full body;
    /// without it, that preview honestly reports the ETag blocker. 404 when the invoice is unknown.
    /// </summary>
    [HttpGet("{id}/alvys-preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult AlvysPreview(string id, [FromQuery] string? parentLoadEtag)
    {
        var previews = _service.BuildAlvysPreviews(id, parentLoadEtag);
        return previews is null ? NotFound() : Ok(previews);
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
