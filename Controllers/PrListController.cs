
using Microsoft.AspNetCore.Mvc;
using ProQur.AI.Api.Interfaces;

namespace ProQur.AI.Api;

[ApiController]
[Route("api/ai/pr-list")]
public sealed class PrListController : ControllerBase
{
    private readonly IPrReader prReader;

    public PrListController(IPrReader prReader)
    {
        this.prReader = prReader;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string status = "approved",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        int? statusCode = status.ToLowerInvariant() switch
        {
            "approved" => 3,
            "rejected" or "denied" => 4,
            "pending" or "onworkflow" => 12,
            "cancelled" or "canceled" => 19,
            _ => null
        };

        if (statusCode is null)
        {
            return BadRequest(new
            {
                error = "Unsupported status filter."
            });
        }

        if (page < 1 || pageSize < 1 || pageSize > 100)
        {
            return BadRequest(new
            {
                error = "Page must be >= 1 and pageSize must be 1-100."
            });
        }

        var total = await prReader.CountAsync(
            statusCode.Value, cancellationToken);

        var records = await prReader.ListAsync(
            statusCode.Value, page, pageSize, cancellationToken);

        return Ok(new
        {
            status,
            statusCode = statusCode.Value,
            page,
            pageSize,
            total,
            totalPages = (int)Math.Ceiling(
                (double)total / pageSize),
            records = records.Select(pr => new
            {
                pr.PRHeadId,
                pr.Number,
                pr.Name,
                pr.RequesterName,
                pr.EstimatedCost,
                pr.CurrencyCode,
                pr.DocumentDate,
                pr.ReqStatusId
            })
        });
    }
}
