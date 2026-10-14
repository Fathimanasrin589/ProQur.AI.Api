namespace ProQur.AI.Api;

public static class PrStatusLabels
{
    public static string Get(int? code) => code switch
    {
        null => "Unknown",
        1 => "Created",
        2 => "Submitted",
        3 => "Approved",
        4 => "Denied",
        5 => "RFQ created",
        6 => "RFQ submitted",
        7 => "Tender",
        9 => "Approval matrix configuration not found",
        10 => "Requisition matrix configuration not found",
        11 => "Buyer matrix configuration not found",
        12 => "On workflow (ongoing approval process)",
        13 => "Review",
        14 => "Update by tender",
        15 => "Created by RFQ supplier",
        16 => "In review",
        17 => "Reviewed",
        18 => "Closed",
        19 => "Cancel",
        20 => "Open",
        _ => "Unmapped status code"
    };
}