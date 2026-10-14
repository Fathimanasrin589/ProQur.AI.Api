using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using ProQur.AI.Api.Interfaces;
using ProQur.AI.Api.Repositories;

namespace ProQur.AI.Api.Controllers;

[ApiController]
[Route("api/ai")]
public sealed class PrApprovalController(
    IPrReader reader,
    SqlPrApprovalReader approvals) : ControllerBase
{
    [HttpGet("pr/{prHeadId:long}/approvals")]
    public async Task<IActionResult> GetApprovals(
        long prHeadId,
        CancellationToken cancellationToken)
    {
        if (prHeadId <= 0)
            return BadRequest(new { message = "Enter a positive PR ID." });

        try
        {
            var facts = await reader.GetAsync(
                prHeadId, cancellationToken);

            if (facts is null)
            {
                return NotFound(new
                {
                    message = "No PR found in the configured company and entity scope."
                });
            }

            var approval = await approvals.GetAsync(
                facts, cancellationToken);

            return Ok(new
            {
                prHeadId,
                facts.CompanyId,
                facts.EntityId,
                approvalDetailsAvailable = approval.RoutingHead is not null,
                approval.RoutingHead,
                approval.Assignments,
                approval.AssignmentCount,
                approval.InProgressCount,
                summary = SqlPrApprovalReader.Describe(approval),
                retrievedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (SqlException)
        {
            return StatusCode(503, new
            {
                message = "The database lookup failed. Try again later."
            });
        }
    }
}