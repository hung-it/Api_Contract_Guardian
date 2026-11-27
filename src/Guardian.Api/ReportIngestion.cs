using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Guardian.Api;

internal static class ReportIngestion
{
    internal const int MaxReportBytes = 10 * 1024 * 1024;
    private static readonly JsonSchema Schema = LoadSchema();

    internal static async Task<IResult> Publish(Guid projectId, HttpRequest request, GuardianDb db,
        CancellationToken cancellationToken)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var authorization)
            || !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || authorization.Parameter is not { } raw || !Bootstrap.IsToken(raw))
        {
            return Results.Unauthorized();
        }
        var hash = Bootstrap.Hash(raw);
        var token = await db.Tokens.AsNoTracking().SingleOrDefaultAsync(row => row.TokenHash == hash, cancellationToken);
        if (Authorize(token, projectId) is { } denied)
        {
            return denied;
        }
        if (!request.HasJsonContentType())
        {
            return Results.Problem(statusCode: 415, title: "Report must be JSON.");
        }
        if (request.ContentLength > MaxReportBytes)
        {
            return Results.Problem(statusCode: 413, title: "Report exceeds 10 MiB.");
        }

        try
        {
            using var report = await JsonDocument.ParseAsync(request.Body,
                new JsonDocumentOptions { AllowDuplicateProperties = false }, cancellationToken);
            var root = report.RootElement;
            if (!Schema.Evaluate(root).IsValid)
            {
                return Results.Problem(statusCode: 422, title: "Report does not match schema v1.");
            }
            return await Persist(projectId, root, hash, db, cancellationToken);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 400, title: "Invalid JSON report.");
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        {
            return Results.Problem(statusCode: 413, title: "Report exceeds 10 MiB.");
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: "22P05" or "22003" or PostgresErrorCodes.ProgramLimitExceeded })
        {
            return Results.Problem(statusCode: 422, title: "Report contains values unsupported by PostgreSQL.");
        }
    }

    private static async Task<IResult> Persist(Guid projectId, JsonElement root, string tokenHash, GuardianDb db,
        CancellationToken cancellationToken)
    {
        var received = DateTimeOffset.UtcNow;
        var analysis = new Analysis
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            AnalysisKey = AnalysisKey(root),
            Status = root.GetProperty("status").GetString()!,
            Report = root.GetRawText(),
            // Match PostgreSQL timestamp precision in the initial receipt and all replays.
            CreatedAt = received.AddTicks(-(received.Ticks % TimeSpan.TicksPerMicrosecond)),
        };
        foreach (var value in root.GetProperty("findings").EnumerateArray())
        {
            analysis.Findings.Add(new()
            {
                Ordinal = analysis.Findings.Count,
                RuleId = value.GetProperty("rule_id").GetString()!,
                Severity = value.GetProperty("severity").GetString()!,
                Detail = value.GetRawText(),
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tokens = await db.Tokens.FromSqlInterpolated(
            $"SELECT * FROM integration_tokens WHERE token_hash = {tokenHash} FOR SHARE")
            .AsNoTracking().ToListAsync(cancellationToken);
        var token = tokens.SingleOrDefault();
        if (Authorize(token, projectId) is { } denied)
        {
            return denied;
        }
        db.Analyses.Add(analysis);
        TokenEndpoints.Audit(db, projectId, token!.Id, "integration_token", "analysis.published", analysis.Id);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Created($"/api/v1/analyses/{analysis.Id}", Response(analysis));
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_analyses_project_key" })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var existing = await db.Analyses.AsNoTracking().SingleAsync(
                row => row.ProjectId == projectId && row.AnalysisKey == analysis.AnalysisKey, cancellationToken);
            var same = await db.Database.SqlQuery<bool>(
                $"SELECT report = CAST({analysis.Report} AS jsonb) AS \"Value\" FROM analyses WHERE id = {existing.Id}")
                .SingleAsync(cancellationToken);
            return same ? Results.Ok(Response(existing))
                : Results.Problem(statusCode: 409, title: "Analysis key already has a different report.");
        }
    }

    private static object Response(Analysis analysis) => new
    {
        analysis_id = analysis.Id,
        status = analysis.Status,
        created_at = analysis.CreatedAt,
    };

    private static IResult? Authorize(IntegrationToken? token, Guid projectId)
    {
        if (token is null || token.RevokedAt.HasValue || token.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Results.Unauthorized();
        }
        return token.ProjectId != projectId || token.Scope != Bootstrap.WriteScope
            ? Results.StatusCode(403) : null;
    }

    private static string AnalysisKey(JsonElement root)
    {
        var provenance = root.GetProperty("provenance");
        if (provenance.GetProperty("pipeline_id").ValueKind == JsonValueKind.Null)
        {
            return Bootstrap.Hash(CanonicalJson(root)?.ToJsonString() ?? "null");
        }

        string[] fields =
        [
            "pipeline_id", "baseline_commit_sha", "candidate_commit_sha", "baseline_sha256",
            "candidate_sha256", "policy_sha256", "guardian_version", "diff_engine", "diff_engine_version",
        ];
        var identity = fields.Select(field => provenance.GetProperty(field).GetString());
        if (provenance.TryGetProperty("ci_provider", out var provider))
        {
            identity = identity.Append(provider.GetString())
                .Append(provenance.TryGetProperty("ci_job_id", out var job) ? job.GetString() : null);
        }
        return Bootstrap.Hash(JsonSerializer.Serialize(identity));
    }

    private static JsonNode? CanonicalJson(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(root.EnumerateObject().OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => KeyValuePair.Create(field.Name, CanonicalJson(field.Value)))),
        JsonValueKind.Array => new JsonArray(root.EnumerateArray().Select(CanonicalJson).ToArray()),
        _ => JsonNode.Parse(root.GetRawText()),
    };

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(ReportIngestion).Assembly.GetManifestResourceStream("Guardian.ReportV1")!;
        using var document = JsonDocument.Parse(stream);
        return JsonSchema.Build(document.RootElement.Clone());
    }
}
