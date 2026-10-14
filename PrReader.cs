namespace ProQur.AI.Api;

// Only the minimum facts required for the first exercise.
// ReqStatusId is a code; no real status label has been verified yet.
public sealed record PrFacts(
    long PRHeadId,
    int CompanyId,
    int EntityId,
    string Number,
    string Name,
    int? ReqStatusId,
    bool? IsDraft,
    bool? IsRejected,

    // Additional PR details
    string? Description = null,
    DateTime? DocumentDate = null,
    DateTime? RequiredDate = null,
    decimal? EstimatedCost = null,
    int? CurrencyId = null,
    int? ReqUserId = null,
    int? BuyerId = null,
    string? RequesterName = null,
    string? CurrencyCode = null,
    string? CurrencyName = null
);

public interface IPrReader
{
    Task<PrFacts?> GetAsync(
        long id,
        CancellationToken cancellationToken);

    Task<PrFacts?> GetByNumberAsync(
        string prNumber,
        CancellationToken cancellationToken);
}

public sealed class SamplePrReader : IPrReader
{
    // Fictional sample data, not actual database records.
    private static readonly PrFacts[] Samples =
    [
        new(101, 1, 1, "DEMO-PR-101", "Five laptops", 999, true, false),
        new(102, 1, 1, "DEMO-PR-102", "Office chairs", 998, false, true),
        new(103, 1, 1, "DEMO-PR-103", "Printer supplies", null, null, null)
    ];

    public Task<PrFacts?> GetAsync(
        long id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<PrFacts?>(
            Samples.FirstOrDefault(x => x.PRHeadId == id));
    }

    public Task<PrFacts?> GetByNumberAsync(
        string prNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Sample reader does not support PR reference lookups.
        return Task.FromResult<PrFacts?>(null);
    }
}