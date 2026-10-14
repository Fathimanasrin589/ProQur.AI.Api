using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using ProQur.AI.Api.Interfaces;
using ProQur.AI.Api.Repositories;
using ProQur.AI.Api.Services;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProQur.AI.Api.Controllers;

public sealed class AiQuestionRequest
{
    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string Question { get; set; } = "";

    // Allows comparison with and without the model explanation.
    public bool UseModelExplanation { get; set; } = true;
}

[ApiController]
[Route("api/ai")]
public sealed class AiQuestionController(
    IHttpClientFactory clients,
    IPrReader reader,
    SqlPrApprovalReader approvals,
    LocalModelClient model) : ControllerBase
{
    [HttpPost("interpret")]
    public async Task<IActionResult> Interpret(
        AiQuestionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { message = "Enter a question." });

        var body = new
        {
            model = "qwen3:1.7b",
            stream = false,
            think = false,
            format = "json",
            options = new
            {
                temperature = 0,
                num_ctx = 2048,
                num_predict = 150
            },
            messages = new[]
    {
        new
        {
            role = "system",
            content =
                "Classify the user's procurement question. " +
                "Return ONLY JSON with exactly four fields: " +
                "intent, identifierType, identifier, statusFilter. " +

                "Allowed intents: pr_status, pr_details, pr_list, unsupported. " +

                "Use pr_list when the user asks to list, show, " +
                "count, or retrieve multiple PRs based on status. " +

                "Allowed statusFilter values: approved, rejected, " +
                "pending, cancelled, null. " +

                "For pr_list, set identifierType and identifier to null. " +
                "Set statusFilter to the requested status. " +
                "Pending means on-workflow status, not every unfinished PR. " +

                "Use pr_status when the user asks about the status " +
                "of a purchase requisition. " +

                "Use pr_details when the user asks what a PR is " +
                "or requests general details of a specific PR. " +

                "Allowed identifierType values: prHeadId, prNumber, null. " +

                "Use prHeadId for a numeric internal PR head ID. " +
                "Use prNumber for a PR reference starting with PR-. " +

                "Preserve the identifier exactly as provided. " +
                "Never invent or modify identifiers. " +

                "For missing identifiers, multiple identifiers, " +
                "or requests to modify data, use unsupported. " +

                "For unsupported return identifierType null " +
                "and identifier null. " +


                "Examples: " +

                "Question: What is the status of PR 43965? " +
                "Output: {\"intent\":\"pr_status\"," +
                "\"identifierType\":\"prHeadId\"," +
                "\"identifier\":\"43965\"," +
                "\"statusFilter\":null}. " +

                "Question: What is PR-0011111111111111202? " +
                "Output: {\"intent\":\"pr_details\"," +
                "\"identifierType\":\"prNumber\"," +
                "\"identifier\":\"PR-0011111111111111202\"," +
                "\"statusFilter\":null}. " +

                "Question: Is PR-0011111111111111202 approved? " +
                "Output: {\"intent\":\"pr_status\"," +
                "\"identifierType\":\"prNumber\"," +
                "\"identifier\":\"PR-0011111111111111202\"," +
                "\"statusFilter\":null}. " +

                "Question: Show approved PRs. " +
                "Output: {\"intent\":\"pr_list\"," +
                "\"identifierType\":null," +
                "\"identifier\":null," +
                "\"statusFilter\":\"approved\"}. " +

                "Question: Show pending PRs. " +
                "Output: {\"intent\":\"pr_list\"," +
                "\"identifierType\":null," +
                "\"identifier\":null," +
                "\"statusFilter\":\"pending\"}. " +

                "Do not answer the user's question."


        },
        new
        {
            role = "user",
            content = request.Question
        }
    }
        };

        long? requestedPrHeadId = null;
        string? requestedPrNumber = null;
        string? requestedIntent = null;

        // Step 1: Interpret the question and check the returned ID.
        try
        {
            var client = clients.CreateClient("OllamaIntent");

            using var response = await client.PostAsJsonAsync(
                "api/chat",
                body,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            using var envelope = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));

            var root = envelope.RootElement;

            if (!root.TryGetProperty("done", out var done)
                || done.ValueKind != JsonValueKind.True
                || (root.TryGetProperty("done_reason", out var reason)
                    && reason.GetString() == "length"))
            {
                return StatusCode(502, new
                {
                    message = "Qwen returned an incomplete response."
                });
            }

            var content = root
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            using var parsed = JsonDocument.Parse(content ?? "");
            var result = parsed.RootElement;

            // Reject malformed output rather than trusting it.
            // Validate Qwen's JSON structure.
            // Validate Qwen's JSON structure.
            if (result.ValueKind != JsonValueKind.Object
                || result.EnumerateObject().Count() != 4
                || !result.TryGetProperty("intent", out var intentElement)
                || !result.TryGetProperty("identifierType", out var typeElement)
                || !result.TryGetProperty("identifier", out var identifierElement)
                || !result.TryGetProperty("statusFilter", out var statusFilterElement)
                || intentElement.ValueKind != JsonValueKind.String)
            {
                return StatusCode(502, new
                {
                    message = "Qwen returned an invalid interpretation."
                });
            }

            var intent = intentElement.GetString();
            if (intent == "pr_list")
            {
                if (typeElement.ValueKind != JsonValueKind.Null ||
                    identifierElement.ValueKind != JsonValueKind.Null ||
                    statusFilterElement.ValueKind != JsonValueKind.String)
                {
                    return BadRequest(new
                    {
                        message = "Invalid PR listing request."
                    });
                }

                var statusFilter = statusFilterElement.GetString();

                int? statusCode = statusFilter switch
                {
                    "approved" => 3,
                    "rejected" => 4,
                    "pending" => 12,
                    "cancelled" => 19,
                    _ => null
                };

                if (statusCode is null)
                {
                    return BadRequest(new
                    {
                        message = "Unsupported PR status filter."
                    });
                }
                // Detect whether the user wants only the number of PRs.
                bool countOnly = Regex.IsMatch(
                    request.Question,
                    @"(?i)\b(how many|count|number of|total)\b");
                // Page 1 by default.
                // Default to the first page.
                int page = 1;

                // Detect "page 2", "page 3", etc.
                var pageMatch = Regex.Match(
                    request.Question,
                    @"(?i)\bpage\s+(\d+)\b"
                );

                if (pageMatch.Success)
                {
                    if (!int.TryParse(pageMatch.Groups[1].Value, out page)
                        || page < 1
                        || page > 10000)
                    {
                        return BadRequest(new
                        {
                            message = "Invalid page number."
                        });
                    }
                }

                // Default to 20 PRs unless the user requests a limit.
                int pageSize = 20;

                // Detect questions such as "Show 10 approved PRs".
                var limitMatch = Regex.Match(
                    request.Question,
                    @"(?i)\b(?:show|list|display|give me|latest|top)\s+(?:me\s+)?(?:the\s+)?(?:latest\s+|top\s+|first\s+)?(\d{1,3})\b"
                );


                if (limitMatch.Success &&
                    int.TryParse(limitMatch.Groups[1].Value, out int requestedLimit))
                {
                    if (requestedLimit < 1 || requestedLimit > 100)
                    {
                        return BadRequest(new
                        {
                            message = "You can request between 1 and 100 PRs."
                        });
                    }

                    pageSize = requestedLimit;
                }

                try
                {
                    var total = await reader.CountAsync(
                        statusCode.Value,
                        cancellationToken);
                    if (countOnly)
                    {
                        return Ok(new
                        {
                            intent = "pr_list",
                            operation = "count",
                            status = statusFilter,
                            statusCode = statusCode.Value,
                            total,
                            databaseQueried = true,
                            answer = $"There are {total} {statusFilter} PRs.",
                            retrievedAtUtc = DateTimeOffset.UtcNow
                        });
                    }

                    var records = await reader.ListAsync(
                        statusCode.Value,
                        page,
                        pageSize,
                        cancellationToken);

                    return Ok(new
                    {
                        intent = "pr_list",
                        status = statusFilter,
                        statusCode = statusCode.Value,
                        page,
                        pageSize,
                        total,
                        totalPages = (int)Math.Ceiling(
                            (double)total / pageSize),
                        databaseQueried = true,
                        dataSource = "Procqur_QA database",
                        answer = $"Found {total} {statusFilter} PRs. " +
                                 $"Showing {records.Count} records.",
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
                        }),
                        retrievedAtUtc = DateTimeOffset.UtcNow
                    });
                }
                catch (SqlException)
                {
                    return StatusCode(503, new
                    {
                        message = "PR listing database query failed."
                    });
                }
            }

            if (intent != "pr_status" && intent != "pr_details")
            {
                return Ok(new
                {
                    intent = "unsupported",
                    databaseQueried = false,
                    message = "Ask about one specific purchase requisition."
                });
            }

            if (typeElement.ValueKind != JsonValueKind.String
                || identifierElement.ValueKind != JsonValueKind.String)
            {
                return Ok(new
                {
                    intent = "unsupported",
                    databaseQueried = false,
                    message = "A valid PR identifier is required."
                });
            }

            var identifierType = typeElement.GetString();
            var identifier = identifierElement.GetString();

            if (string.IsNullOrWhiteSpace(identifier))
            {
                return BadRequest(new
                {
                    message = "PR identifier is missing."
                });
            }

            // Extract PR numbers and numeric IDs independently from the question.
            var prNumbers = Regex.Matches(
                request.Question,
                @"(?i)(?<![A-Za-z0-9])PR-\d+(?![A-Za-z0-9])");

            var questionWithoutPrNumbers = Regex.Replace(
                request.Question,
                @"(?i)(?<![A-Za-z0-9])PR-\d+(?![A-Za-z0-9])",
                " ");

            var numericIds = Regex.Matches(
                questionWithoutPrNumbers,
                @"(?<![A-Za-z0-9])\d+(?![A-Za-z0-9])");

            // Require exactly one identifier in the original question.
            if (prNumbers.Count + numericIds.Count != 1)
            {
                return Ok(new
                {
                    intent = "unsupported",
                    databaseQueried = false,
                    message = "Provide exactly one PR identifier."
                });
            }

            if (identifierType == "prHeadId"
                && numericIds.Count == 1
                && long.TryParse(identifier, out var parsedId)
                && parsedId > 0
                && long.TryParse(numericIds[0].Value, out var suppliedId)
                && parsedId == suppliedId
                && identifier == numericIds[0].Value)
            {
                requestedPrHeadId = parsedId;
            }
            else if (identifierType == "prNumber"
                && prNumbers.Count == 1
                && string.Equals(
                    identifier,
                    prNumbers[0].Value,
                    StringComparison.Ordinal))
            {
                requestedPrNumber = identifier;
            }
            else
            {
                return Ok(new
                {
                    intent = "unsupported",
                    databaseQueried = false,
                    message = "The interpreted PR identifier did not match the question."
                });
            }

            requestedIntent = intent;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504, new
            {
                message = "Ollama timed out."
            });
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException
            or FormatException)
        {
            return StatusCode(502, new
            {
                message = "Ollama is unavailable or returned an invalid response."
            });
        }

        // Step 2: Retrieve the PR using the configured scope.
        PrFacts? facts;

        try
        {
            if (requestedPrHeadId.HasValue)
            {
                facts = await reader.GetAsync(
                    requestedPrHeadId.Value,
                    cancellationToken);
            }
            else if (requestedPrNumber is not null)
            {
                facts = await reader.GetByNumberAsync(
                    requestedPrNumber,
                    cancellationToken);
            }
            else
            {
                return BadRequest(new
                {
                    message = "A valid PR identifier is required."
                });
            }
        }
        catch (SqlException)
        {
            return StatusCode(503, new
            {
                message = "The PR database lookup failed. Try again later."
            });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new
            {
                message = "The PR reference could not be resolved uniquely."
            });
        }

        if (facts is null)
        {
            return NotFound(new
            {
                message = "No PR found in the configured company and entity scope."
            });
        }

        // Resolve the actual internal PR ID from the database.
        long prHeadId = facts.PRHeadId;

        // Step 3: Retrieve approval facts.
        PrApprovalFacts? approval = null;
        string? approvalWarning = null;

        try
        {
            approval = await approvals.GetAsync(
                facts,
                cancellationToken);
        }
        catch (SqlException)
        {
            approvalWarning =
                "Approval lookup failed; the PR status is still available.";
        }

        // Capture when the database reads finished, before model generation.
        var retrievedAtUtc = DateTimeOffset.UtcNow;

        var approvalSummary = approval is null
            ? "Approval details could not be retrieved."
            : SqlPrApprovalReader.Describe(approval);

        // Step 4: Keep the factual C# answer.
        // Step 4: Generate a factual answer based on the question.
        string answer;

        if (requestedIntent == "pr_details")
        {
            answer =
                $"Purchase Requisition: {facts.Number}\n" +
                $"PR Head ID: {facts.PRHeadId}\n" +
                $"Name: {facts.Name}\n" +
                $"Description: {facts.Description ?? "Not available"}\n" +
                $"Document Date: {FormatDate(facts.DocumentDate)}\n" +
                $"Required Date: {FormatDate(facts.RequiredDate)}\n" +
                $"Estimated Cost: {FormatAmount(facts.EstimatedCost)}\n" +
                $"Currency ID: {facts.CurrencyId?.ToString() ?? "Not available"}\n" +
                $"Requester ID: {facts.ReqUserId?.ToString() ?? "Not available"}\n" +
                $"Buyer ID: {facts.BuyerId?.ToString() ?? "Not assigned"}\n" +
                $"Status: {PrStatusLabels.Get(facts.ReqStatusId)} " +
                $"(Code {facts.ReqStatusId?.ToString() ?? "unknown"})\n" +
                $"Is Draft: {Display(facts.IsDraft)}\n" +
                $"Is Rejected: {Display(facts.IsRejected)}";
        }

        else
        {
            answer =
                $"{facts.Number}: " +
                $"Status={PrStatusLabels.Get(facts.ReqStatusId)} " +
                $"(code {facts.ReqStatusId?.ToString() ?? "unknown"}). " +
                $"IsDraft={Display(facts.IsDraft)}, " +
                $"IsRejected={Display(facts.IsRejected)}. " +
                approvalSummary +
                " A specific blocker, next action, and linked PO have not been retrieved.";
        }
        // Give direct Yes/No answers for specific PR status questions.
        if (requestedIntent == "pr_status")
        {
            var asksRejected = Regex.IsMatch(
                request.Question,
                @"(?i)\b(rejected|reject|denied)\b");

            var asksCancelled = Regex.IsMatch(
                request.Question,
                @"(?i)\b(cancelled|canceled)\b");

            if (asksRejected && !asksCancelled)
            {
                if (facts.ReqStatusId == 4)
                {
                    answer = $"Yes. PR {facts.Number} has Denied status (code 4).";
                }
                else if (facts.IsRejected == true)
                {
                    answer = $"Yes. PR {facts.Number} is marked as rejected.";
                }
                else if (facts.IsRejected == false && facts.ReqStatusId.HasValue)
                {
                    answer =
                        $"No. PR {facts.Number} is not marked as rejected. " +
                        $"Current status: {PrStatusLabels.Get(facts.ReqStatusId)} " +
                        $"(code {facts.ReqStatusId}).";
                }
                else
                {
                    answer = $"The rejection status of PR {facts.Number} cannot be confirmed.";
                }
            }
            else if (asksCancelled && !asksRejected)
            {
                if (facts.ReqStatusId == 19)
                {
                    answer = $"Yes. PR {facts.Number} is currently cancelled.";
                }
                else if (facts.ReqStatusId.HasValue)
                {
                    answer =
                        $"No. PR {facts.Number} is not currently cancelled. " +
                        $"Current status: {PrStatusLabels.Get(facts.ReqStatusId)} " +
                        $"(code {facts.ReqStatusId}).";
                }
                else
                {
                    answer = $"The cancellation status of PR {facts.Number} cannot be confirmed.";
                }
            }
        }
        // Step 5: Ask Qwen to explain the retrieved facts.
        string? modelExplanation = null;
        string? warning = null;

        if (request.UseModelExplanation)
        {
            try
            {
                modelExplanation = await model.ExplainAsync(
                    facts,
                    approval,
                    cancellationToken);

                warning = string.IsNullOrWhiteSpace(modelExplanation)
                    ? "No complete model explanation returned. Use the factual answer."
                    : "The model explanation is unverified. Check it against the factual answer.";
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                warning =
                    "The model explanation timed out. Use the factual answer.";
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or JsonException
                or KeyNotFoundException
                or InvalidOperationException
                or FormatException)
            {
                warning =
                    "The local model was unavailable or returned an invalid explanation. " +
                    "Use the factual answer.";
            }
        }

        return Ok(new
        {
            intent = requestedIntent,
            identifierType = requestedPrNumber is not null
        ? "prNumber"
        : "prHeadId",
            requestedIdentifier = requestedPrNumber
        ?? requestedPrHeadId?.ToString(),
            prHeadId,
            interpretationIsVerified = false,
            databaseQueried = true,
            dataSource = "Procqur_QA database",
            answerSource = "C# formatting of retrieved database facts",
            answer,
            facts,
            approval,
            approvalWarning,
            modelExplanation,
            modelExplanationIsVerified = false,
            warning,
            retrievedAtUtc
        });
    }

    private static string Display(bool? value)
        => value?.ToString() ?? "unknown";
    private static string FormatDate(DateTime? value)
    => value?.ToString("dd MMM yyyy") ?? "Not available";

    private static string FormatAmount(decimal? value)
        => value?.ToString("N2") ?? "Not available";
}