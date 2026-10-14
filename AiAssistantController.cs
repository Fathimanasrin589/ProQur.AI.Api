using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace ProQur.AI.Api;

public sealed class PrStatusRequest
{
    [Range(1, long.MaxValue)]
    public long PRHeadId { get; set; }

    public bool UseModel { get; set; } = false;
}

[ApiController]
[Route("api/ai")]
public sealed class AiAssistantController(IPrReader reader) : ControllerBase
{
    [HttpPost("pr-status")]
    public async Task<IActionResult> GetStatus(
        PrStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (request.UseModel)
        {
            return BadRequest(new
            {
                message = "For the initial database test, set useModel to false."
            });
        }

        var facts = await reader.GetAsync(
            request.PRHeadId, cancellationToken);

        if (facts is null)
        {
            return NotFound(new
            {
                message = "No PR found in the configured company and entity scope."
            });
        }

        var answer =
                    $"{facts.Number}: " +
                    $"Status={PrStatusLabels.Get(facts.ReqStatusId)} " +
                    $"(code {facts.ReqStatusId?.ToString() ?? "unknown"}). " +
                    $"IsDraft={Display(facts.IsDraft)}, " +
                    $"IsRejected={Display(facts.IsRejected)}. " +
                    "Approval owner, blocker, next action, and linked PO are unavailable.";

        return Ok(new
        {
            dataSource = "Procqur_QA database",
            answer,
            facts,
            retrievedAtUtc = DateTimeOffset.UtcNow,
            modelExplanation = (string?)null,
            modelExplanationIsVerified = false,
            warning = (string?)null
        });
    }

    private static string Display(bool? value)
        => value?.ToString() ?? "unknown";
}