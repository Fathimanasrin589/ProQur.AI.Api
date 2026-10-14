namespace ProQur.AI.Api.Interfaces;

public interface IPrReader
{
    Task<PrFacts?> GetAsync(
        long id,
        CancellationToken cancellationToken);

    Task<PrFacts?> GetByNumberAsync(
        string prNumber,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PrFacts>> ListAsync(
        int statusCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<int> CountAsync(
        int statusCode,
        CancellationToken cancellationToken);
}