using Dapper;
using Microsoft.Data.SqlClient;

namespace ProQur.AI.Api;

public sealed class PrApprovalFacts
{
    public PrRoutingHead? RoutingHead { get; init; }
    public List<PrApprovalAssignment> Assignments { get; init; } = [];

    public int AssignmentCount => Assignments.Count;
    public int InProgressCount => Assignments.Count(x => x.StatusId == 2);
}

public sealed class PrRoutingHead
{
    public int RoutingHeadId { get; set; }
    public int Level { get; set; }
    public string? DocumentStatus { get; set; }
    public bool? IsLevelCompleted { get; set; }
    public bool? IsActive { get; set; }
    public bool? IsDeleted { get; set; }
}

public sealed class PrApprovalAssignment
{
    public int AssignmentId { get; set; }
    public int? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public int? ApprovalLevel { get; set; }
    public int? StatusId { get; set; }
    public bool? EmployeeIsActive { get; set; }
    public bool? EmployeeIsDeleted { get; set; }

    public string StatusLabel => StatusId switch
    {
        1 => "Pending",
        2 => "InProgress",
        3 => "Approved",
        4 => "Rejected",
        5 => "Approved By Another User",
        6 => "Rejected By Another User",
        7 => "Cancelled",
        null => "Unknown",
        _ => "Unmapped"
    };
}

public sealed class SqlPrApprovalReader(IConfiguration configuration)
{
    public async Task<PrApprovalFacts> GetAsync(
        PrFacts facts,
        CancellationToken cancellationToken)
    {
        const string headSql = """
            SELECT TOP (1)
                h.Id AS RoutingHeadId,
                h.Level,
                h.DocumentStatus,
                h.IsLevelCompleted,
                h.IsActive,
                h.IsDeleted
            FROM Approval.ApprovalMatrixRutingHead AS h
            INNER JOIN Approval.ApprovalMatrix AS m
                ON m.Id = h.ApprovalMatrixId
            INNER JOIN Procur.PRHead AS pr
                ON pr.PRHeadId = h.DocumnetId
               AND pr.CompanyId = m.CompanyId
            WHERE h.BusinessObject = 1
              AND pr.PRHeadId = @PRHeadId
              AND pr.CompanyId = @CompanyId
              AND pr.EntityId = @EntityId
              AND (pr.IsDeleted = 0 OR pr.IsDeleted IS NULL)
            ORDER BY h.Id DESC;
            """;

        const string assignmentSql = """
            SELECT
                d.Id AS AssignmentId,
                d.EmployeeId,
                CASE WHEN e.Id IS NULL THEN NULL ELSE
                    LTRIM(RTRIM(CONCAT(
                        e.FirstName, ' ',
                        NULLIF(e.MiddleName, ''), ' ',
                        e.LastName
                    )))
                END AS EmployeeName,
                d.ApprovalLevel,
                d.StatusId,
                e.IsActive AS EmployeeIsActive,
                e.IsDeleted AS EmployeeIsDeleted
            FROM Approval.ApprovalMatrixRutingDetails AS d
            LEFT JOIN Master.Employee AS e
                ON e.Id = d.EmployeeId
               AND e.CompanyId = d.CompanyId
            WHERE d.ApprovalMatrixRutingHeadId = @RoutingHeadId
              AND d.DocumentId = @PRHeadId
              AND d.CompanyId = @CompanyId
              AND d.EntityId = @EntityId
              AND d.ApprovalLevel = @Level
              AND d.IsActive = 1
              AND (d.IsDeleted = 0 OR d.IsDeleted IS NULL)
            ORDER BY d.Id;
            """;

        var connectionString =
            configuration.GetConnectionString("ProQurDb")
            ?? throw new InvalidOperationException(
                "ProQurDb connection string is missing.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var head = await connection.QueryFirstOrDefaultAsync<PrRoutingHead>(
            new CommandDefinition(
                headSql,
                new
                {
                    facts.PRHeadId,
                    facts.CompanyId,
                    facts.EntityId
                },
                commandTimeout: 30,
                cancellationToken: cancellationToken));

        if (head is null)
            return new PrApprovalFacts();

        var assignments = await connection.QueryAsync<PrApprovalAssignment>(
            new CommandDefinition(
                assignmentSql,
                new
                {
                    head.RoutingHeadId,
                    facts.PRHeadId,
                    facts.CompanyId,
                    facts.EntityId,
                    head.Level
                },
                commandTimeout: 30,
                cancellationToken: cancellationToken));

        return new PrApprovalFacts
        {
            RoutingHead = head,
            Assignments = assignments.ToList()
        };
    }

    public static string Describe(PrApprovalFacts approval)
    {
        var head = approval.RoutingHead;

        if (head is null)
            return "No approval routing header was found.";

        var details =
            $"Latest routing header: status={head.DocumentStatus ?? "unknown"}, " +
            $"level={head.Level}. ";

        var isCurrent =
            head.IsActive == true &&
            head.IsDeleted == false &&
            head.IsLevelCompleted == false &&
            string.Equals(
                head.DocumentStatus,
                "InProgress",
                StringComparison.OrdinalIgnoreCase);

        if (!isCurrent)
        {
            return details +
                "This header is not confirmed as a current in-progress route.";
        }

        return details +
            $"{approval.AssignmentCount} active, non-deleted assignments " +
            $"at this level; {approval.InProgressCount} marked InProgress.";
    }
}