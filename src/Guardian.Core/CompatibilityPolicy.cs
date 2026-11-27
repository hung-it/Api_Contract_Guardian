namespace Guardian.Core;

public enum AnalysisStatus
{
    PASS,
    FAIL,
    INDETERMINATE,
}

public sealed record PolicyDecision(AnalysisStatus Status, ContractFinding[] Findings);

public sealed class CompatibilityPolicy
{
    private static readonly HashSet<string> KnownRules = new(StringComparer.Ordinal)
    {
        "endpoint-added",
        "api-path-removed-without-deprecation",
        "api-removed-without-deprecation",
        "request-parameter-became-required",
        "new-required-request-property",
        "new-optional-request-parameter",
        "new-optional-request-property",
        "request-body-additional-properties-disallowed",
        "request-body-media-type-added",
        "request-body-media-type-removed",
        "request-body-optional-property-narrowed",
        "request-property-min-length-decreased",
        "request-property-min-length-increased",
        "request-property-min-length-set",
        "request-property-min-length-unset",
        "request-property-enum-value-removed",
        "response-property-became-optional",
        "response-property-type-changed",
        "response-required-property-removed",
    };

    private readonly Dictionary<string, FindingSeverity> _overrides;

    public CompatibilityPolicy(FindingSeverity threshold, bool failOnUnknown,
        IReadOnlyDictionary<string, FindingSeverity> overrides)
    {
        Threshold = threshold;
        FailOnUnknown = failOnUnknown;
        _overrides = new(overrides, StringComparer.Ordinal);
    }

    public FindingSeverity Threshold { get; }
    public bool FailOnUnknown { get; }

    public static bool IsKnownRule(string ruleId) => KnownRules.Contains(ruleId);

    public PolicyDecision Evaluate(IEnumerable<ContractFinding> findings)
    {
        var effective = findings.Select(finding => finding.Severity != FindingSeverity.UNKNOWN
                && _overrides.TryGetValue(finding.RuleId, out var severity)
            ? finding with { Severity = severity }
            : finding)
            .OrderByDescending(finding => finding.Severity)
            .ToArray();

        if (FailOnUnknown && effective.Any(finding => finding.Severity == FindingSeverity.UNKNOWN))
        {
            return new(AnalysisStatus.INDETERMINATE, effective);
        }

        var blocked = effective.Any(finding => finding.Severity != FindingSeverity.UNKNOWN
            && finding.Severity >= Threshold);
        return new(blocked ? AnalysisStatus.FAIL : AnalysisStatus.PASS, effective);
    }
}
