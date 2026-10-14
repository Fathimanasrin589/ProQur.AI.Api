using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProQur.AI.Api;

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
                "Return ONLY JSON with exactly three fields: " +
                "intent, identifierType, identifier. " +

                "Allowed intents: pr_status, pr_details, unsupported. " +

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
                "\"identifier\":\"43965\"}. " +

                "Question: What is PR-0011111111111111202? " +
                "Output: {\"intent\":\"pr_details\"," +
                "\"identifierType\":\"prNumber\"," +
                "\"identifier\":\"PR-0011111111111111202\"}. " +

                "Question: Is PR-0011111111111111202 approved? " +
                "Output: {\"intent\":\"pr_status\"," +
                "\"identifierType\":\"prNumber\"," +
                "\"identifier\":\"PR-0011111111111111202\"}. " +

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
            if (result.ValueKind != JsonValueKind.Object
                || result.EnumerateObject().Count() != 3
                || !result.TryGetProperty("intent", out var intentElement)
                || !result.TryGetProperty("identifierType", out var typeElement)
                || !result.TryGetProperty("identifier", out var identifierElement)
                || intentElement.ValueKind != JsonValueKind.String)
            {
                return StatusCode(502, new
                {
                    message = "Qwen returned an invalid interpretation."
                });
            }

            var intent = intentElement.GetString();

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