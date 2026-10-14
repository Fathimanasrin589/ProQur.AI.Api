
using Dapper;
using Microsoft.Data.SqlClient;

namespace ProQur.AI.Api;

public sealed class SqlPrReader : IPrReader
{
    private readonly string connectionString;
    private readonly int companyId;
    private readonly int entityId;

    public SqlPrReader(IConfiguration configuration)
    {
        connectionString =
            configuration.GetConnectionString("ProQurDb")
            ?? throw new InvalidOperationException(
                "ProQurDb connection string is missing.");

        companyId = configuration.GetValue<int>("PrReadScope:CompanyId");
        entityId = configuration.GetValue<int>("PrReadScope:EntityId");

        if (companyId <= 0 || entityId <= 0)
        {
            throw new InvalidOperationException(
                "A valid company and entity scope is required.");
        }
    }

    // STEP 1: Find a PR using its internal PRHeadId.
    public async Task<PrFacts?> GetAsync(
        long id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                CAST(PR.PRHeadId AS bigint) AS PRHeadId,
                PR.CompanyId,
                PR.EntityId,
                COALESCE(PR.ReqRefNumber, '') AS Number,
                COALESCE(PR.Name, '') AS Name,
                PR.ReqStatusId,
                PR.IsDraft,
                PR.IsRejected,
                PR.Description,
                PR.[Date] AS DocumentDate,
                PR.RequiredDate,
                PR.EstimatedCost,
                PR.CurrencyId,
                PR.ReqUserId,
                PR.BuyerId,

                NULLIF(
                    LTRIM(RTRIM(CONCAT(
                        E.FirstName, ' ', E.LastName
                    ))),
                    ''
                ) AS RequesterName,

                C.CurrencyCode,
                C.CurrencyName

            FROM Procur.PRHead PR

            LEFT JOIN Master.Employee E
                ON PR.ReqUserId = E.Id
                AND PR.CompanyId = E.CompanyId
                AND (E.IsDeleted = 0 OR E.IsDeleted IS NULL)

            LEFT JOIN Master.Currency C
                ON PR.CurrencyId = C.Id
                AND (C.IsDeleted = 0 OR C.IsDeleted IS NULL)

            WHERE PR.PRHeadId = @Id
              AND PR.CompanyId = @CompanyId
              AND PR.EntityId = @EntityId
              AND (PR.IsDeleted IS NULL OR PR.IsDeleted = 0);
            """;

        await using var connection =
            new SqlConnection(connectionString);

        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(
            sql,
            new
            {
                Id = id,
                CompanyId = companyId,
                EntityId = entityId
            },
            commandTimeout: 30,
            cancellationToken: cancellationToken);

        var row =
            await connection.QuerySingleOrDefaultAsync<PrRow>(command);

        return MapToFacts(row);
    }

    // STEP 2: Find a PR using its business reference number.
    public async Task<PrFacts?> GetByNumberAsync(
        string prNumber,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prNumber))
            return null;

        const string sql = """
            SELECT
                CAST(PR.PRHeadId AS bigint) AS PRHeadId,
                PR.CompanyId,
                PR.EntityId,
                COALESCE(PR.ReqRefNumber, '') AS Number,
                COALESCE(PR.Name, '') AS Name,
                PR.ReqStatusId,
                PR.IsDraft,
                PR.IsRejected,
                PR.Description,
                PR.[Date] AS DocumentDate,
                PR.RequiredDate,
                PR.EstimatedCost,
                PR.CurrencyId,
                PR.ReqUserId,
                PR.BuyerId,

                NULLIF(
                    LTRIM(RTRIM(CONCAT(
                        E.FirstName, ' ', E.LastName
                    ))),
                    ''
                ) AS RequesterName,

                C.CurrencyCode,
                C.CurrencyName

            FROM Procur.PRHead PR

            LEFT JOIN Master.Employee E
                ON PR.ReqUserId = E.Id
                AND PR.CompanyId = E.CompanyId
                AND (E.IsDeleted = 0 OR E.IsDeleted IS NULL)

            LEFT JOIN Master.Currency C
                ON PR.CurrencyId = C.Id
                AND (C.IsDeleted = 0 OR C.IsDeleted IS NULL)

            WHERE PR.ReqRefNumber COLLATE Latin1_General_100_BIN2
                    = @PrNumber COLLATE Latin1_General_100_BIN2
              AND PR.CompanyId = @CompanyId
              AND PR.EntityId = @EntityId
              AND (PR.IsDeleted IS NULL OR PR.IsDeleted = 0);
            """;

        await using var connection =
            new SqlConnection(connectionString);

        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(
            sql,
            new
            {
                PrNumber = prNumber,
                CompanyId = companyId,
                EntityId = entityId
            },
            commandTimeout: 30,
            cancellationToken: cancellationToken);

        // Fetch at most two matching rows to detect ambiguity.
        var rows = (await connection.QueryAsync<PrRow>(command))
            .Take(2)
            .ToList();

        if (rows.Count > 1)
        {
            throw new InvalidOperationException(
                "Multiple PRs match the supplied reference number.");
        }

        return MapToFacts(rows.SingleOrDefault());
    }

    // STEP 3: Convert the database row into our factual model.
    private static PrFacts? MapToFacts(PrRow? row)
    {
        if (row is null)
            return null;

        return new PrFacts(
            row.PRHeadId,
            row.CompanyId,
            row.EntityId,
            row.Number,
            row.Name,
            row.ReqStatusId,
            row.IsDraft,
            row.IsRejected,
            row.Description,
            row.DocumentDate,
            row.RequiredDate,
            row.EstimatedCost,
            row.CurrencyId,
            row.ReqUserId,
            row.BuyerId,
            row.RequesterName,
            row.CurrencyCode,
            row.CurrencyName
        );
    }

    // STEP 4: Dapper mapping model.
    private sealed class PrRow
    {
        public long PRHeadId { get; set; }
        public int CompanyId { get; set; }
        public int EntityId { get; set; }

        public string Number { get; set; } = "";
        public string Name { get; set; } = "";

        public int? ReqStatusId { get; set; }
        public bool? IsDraft { get; set; }
        public bool? IsRejected { get; set; }

        public string? Description { get; set; }
        public DateTime? DocumentDate { get; set; }
        public DateTime? RequiredDate { get; set; }
        public decimal? EstimatedCost { get; set; }

        public int? CurrencyId { get; set; }
        public int? ReqUserId { get; set; }
        public int? BuyerId { get; set; }

        public string? RequesterName { get; set; }
        public string? CurrencyCode { get; set; }
        public string? CurrencyName { get; set; }
    }
}
