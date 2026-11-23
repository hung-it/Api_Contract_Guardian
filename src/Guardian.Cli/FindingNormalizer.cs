using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Guardian.Core;

namespace Guardian.Cli;

internal static class FindingNormalizer
{
    private static readonly HashSet<string> KnownRules = new(StringComparer.Ordinal)
    {
        "endpoint-added",
        "api-path-removed-without-deprecation",
        "api-removed-without-deprecation",
        "request-parameter-became-required",
        "new-required-request-property",
        "request-property-enum-value-removed",
        "response-property-type-changed",
        "response-required-property-removed",
    };

    internal static ContractFinding[] Parse(byte[] output)
    {
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { AllowDuplicateProperties = false });
            return document.RootElement.EnumerateArray()
                .Select(ReadFinding)
                .OrderByDescending(finding => finding.Severity)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ThenBy(finding => finding.Method, StringComparer.Ordinal)
                .ThenBy(finding => finding.Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .ThenBy(finding => finding.Message, StringComparer.Ordinal)
                .ThenBy(finding => finding.BaseSource?.Line)
                .ThenBy(finding => finding.BaseSource?.Column)
                .ThenBy(finding => finding.CandidateSource?.Line)
                .ThenBy(finding => finding.CandidateSource?.Column)
                .ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException
            or KeyNotFoundException)
        {
            throw InvalidOutput();
        }
    }

    private static ContractFinding ReadFinding(JsonElement entry)
    {
        var id = entry.GetProperty("id").GetString();
        var level = entry.GetProperty("level").GetInt32();
        var message = entry.GetProperty("text").GetString();
        var upstreamFingerprint = entry.GetProperty("fingerprint").GetString();
        if (id is null || !Regex.IsMatch(id, @"\A[a-z0-9-]{1,128}\z")
            || level is < 1 or > 3 || message is null || upstreamFingerprint is null
            || !Regex.IsMatch(upstreamFingerprint, @"\A[0-9a-f]{12}\z"))
        {
            throw InvalidOutput();
        }

        var method = OptionalString(entry, "operation")?.ToUpperInvariant();
        var path = OptionalString(entry, "path");
        var section = OptionalString(entry, "section");
        string?[] identity = ["guardian-finding-v1", "oasdiff", id, method, path, section, upstreamFingerprint];
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity));
        var severity = KnownRules.Contains(id) ? (FindingSeverity)level : FindingSeverity.UNKNOWN;

        return new ContractFinding(id, severity, method, path, section,
            ReadLocation(entry, "baseSource"), ReadLocation(entry, "revisionSource"), message,
            $"sha256:{Convert.ToHexStringLower(hash)}");
    }

    private static string? OptionalString(JsonElement entry, string property)
    {
        var value = entry.TryGetProperty(property, out var field) ? field.GetString() : null;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static FindingLocation? ReadLocation(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var location))
        {
            return null;
        }

        int? line = location.TryGetProperty("line", out var lineField) ? lineField.GetInt32() : null;
        int? column = location.TryGetProperty("column", out var columnField) ? columnField.GetInt32() : null;
        return line is > 0 || column is > 0 ? new FindingLocation(line, column) : null;
    }

    private static EngineException InvalidOutput() => new("oasdiff returned invalid changelog JSON.");
}
