using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Guardian.Core;

namespace Guardian.Cli;

internal sealed record ReportProvenance(string GuardianVersion, string DiffEngine, string DiffEngineVersion,
    string? PolicySha256, string? BaselineSha256, string? CandidateSha256, string? BaselineCommitSha,
    string? CandidateCommitSha, string? PipelineId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CiProvider,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CiJobId)
{
    internal static ReportProvenance Create(string version, PolicyFile? policy, SpecDocument? baseline, SpecDocument? candidate) =>
        new(version, "oasdiff", OasdiffAdapter.Version, Hash(policy?.Bytes), Hash(baseline?.Bytes), Hash(candidate?.Bytes),
            Environment.GetEnvironmentVariable("GUARDIAN_BASELINE_SHA"),
            Environment.GetEnvironmentVariable("GUARDIAN_CANDIDATE_SHA"),
            Environment.GetEnvironmentVariable("GUARDIAN_CI_RUN_ID") ?? Environment.GetEnvironmentVariable("CI_PIPELINE_ID"),
            Environment.GetEnvironmentVariable("GUARDIAN_CI_PROVIDER"),
            Environment.GetEnvironmentVariable("GUARDIAN_CI_JOB_ID"));

    private static string? Hash(byte[]? bytes) => bytes is null
        ? null
        : $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
}

internal sealed record ReportSummary(int Errors, int Warnings, int Info, int Unknown);
internal sealed record ReportDiagnostic(string Code, string Message);

internal sealed record AnalysisReport(AnalysisStatus Status, ContractFinding[] Findings,
    ReportProvenance Provenance, ReportDiagnostic[] Diagnostics)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter<FindingSeverity>(), new JsonStringEnumConverter<AnalysisStatus>() },
    };

    public string SchemaVersion => "1.0";
    public string Engine => Provenance.DiffEngine;
    public string EngineVersion => Provenance.DiffEngineVersion;
    public ReportSummary Summary => new(
        Findings.Count(finding => finding.Severity == FindingSeverity.ERR),
        Findings.Count(finding => finding.Severity == FindingSeverity.WARN),
        Findings.Count(finding => finding.Severity == FindingSeverity.INFO),
        Findings.Count(finding => finding.Severity == FindingSeverity.UNKNOWN));

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    internal void Write(string directory, string json)
    {
        var markdown = ToMarkdown();
        Directory.CreateDirectory(directory);
        var jsonTemp = Path.Combine(directory, Path.GetRandomFileName());
        var markdownTemp = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(jsonTemp, json + "\n");
            File.WriteAllText(markdownTemp, markdown);
            File.Move(markdownTemp, Path.Combine(directory, "report.md"), overwrite: true);
            File.Move(jsonTemp, Path.Combine(directory, "report.json"), overwrite: true);
        }
        finally
        {
            File.Delete(jsonTemp);
            File.Delete(markdownTemp);
        }
    }

    private string ToMarkdown()
    {
        var markdown = new StringBuilder("# API Contract Guardian\n\n");
        markdown.Append($"Decision: **{Status}**\n\n");
        markdown.Append($"Guardian: {Escape(Provenance.GuardianVersion)}\n\n");
        markdown.Append($"Engine: {Escape(Engine)} {Escape(EngineVersion)}\n\n");
        markdown.Append($"Baseline SHA-256: {Escape(Provenance.BaselineSha256)}\n\n");
        markdown.Append($"Candidate SHA-256: {Escape(Provenance.CandidateSha256)}\n\n");
        markdown.Append($"Policy SHA-256: {Escape(Provenance.PolicySha256)}\n\n");
        markdown.Append($"Baseline commit: {Escape(Provenance.BaselineCommitSha)}\n\n");
        markdown.Append($"Candidate commit: {Escape(Provenance.CandidateCommitSha)}\n\n");
        markdown.Append($"Pipeline: {Escape(Provenance.PipelineId)}\n\n");
        var summary = Summary;
        markdown.Append($"Findings: {summary.Errors} ERR, {summary.Warnings} WARN, {summary.Info} INFO, {summary.Unknown} UNKNOWN\n\n");
        foreach (var diagnostic in Diagnostics)
        {
            markdown.Append($"- {Escape(diagnostic.Code)}: {Escape(diagnostic.Message)}\n");
        }
        if (Findings.Length > 0)
        {
            markdown.Append("\n| Severity | Rule | Endpoint | Explanation |\n|---|---|---|---|\n");
            foreach (var finding in Findings)
            {
                markdown.Append($"| {finding.Severity} | {Escape(finding.RuleId)} | {Escape($"{finding.Method} {finding.Path}".Trim())} | {Escape(finding.Message)} |\n");
            }
        }
        return markdown.ToString();
    }

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? "Unavailable")
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal)
        .Replace("`", "&#96;", StringComparison.Ordinal)
        .Replace("[", "&#91;", StringComparison.Ordinal)
        .Replace("]", "&#93;", StringComparison.Ordinal)
        .Replace("*", "&#42;", StringComparison.Ordinal)
        .Replace("_", "&#95;", StringComparison.Ordinal);
}
