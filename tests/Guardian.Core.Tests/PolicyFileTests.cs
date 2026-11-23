using System.Text;
using Guardian.Cli;
using Xunit;

namespace Guardian.Core.Tests;

public sealed class PolicyFileTests
{
    [Theory]
    [InlineData("ERR", FindingSeverity.INFO, AnalysisStatus.PASS)]
    [InlineData("ERR", FindingSeverity.WARN, AnalysisStatus.PASS)]
    [InlineData("ERR", FindingSeverity.ERR, AnalysisStatus.FAIL)]
    [InlineData("WARN", FindingSeverity.WARN, AnalysisStatus.FAIL)]
    [InlineData("ERR, WARN", FindingSeverity.ERR, AnalysisStatus.FAIL)]
    [InlineData("INFO", FindingSeverity.INFO, AnalysisStatus.FAIL)]
    [InlineData("ERR", FindingSeverity.UNKNOWN, AnalysisStatus.INDETERMINATE)]
    public void AppliesThresholdAndStrictUnknown(string threshold, FindingSeverity severity, AnalysisStatus expected)
    {
        var policy = Parse($"version: 1\ncompatibility: {{fail_on: [{threshold}], fail_on_unknown: true}}");

        Assert.Equal(expected, policy.Evaluate([Finding(severity)]).Status);
    }

    [Fact]
    public void AppliesOverrideBeforeEvaluatingAndRetainsUnknownWhenRelaxed()
    {
        var policy = Parse("""
            version: 1
            compatibility: {fail_on: [ERR], fail_on_unknown: false}
            rules: {response-property-type-changed: INFO}
            """);
        var decision = policy.Evaluate([Finding(FindingSeverity.ERR), Finding(FindingSeverity.UNKNOWN)]);

        Assert.Equal(AnalysisStatus.PASS, decision.Status);
        Assert.Equal(FindingSeverity.UNKNOWN, decision.Findings[0].Severity);
        Assert.Equal(FindingSeverity.INFO, decision.Findings[1].Severity);
    }

    [Theory]
    [InlineData("version: 2\ncompatibility: {fail_on: [ERR], fail_on_unknown: true}")]
    [InlineData("version: \"1\"\ncompatibility: {fail_on: [ERR], fail_on_unknown: true}")]
    [InlineData("version: 1\nversion: 1\ncompatibility: {fail_on: [ERR], fail_on_unknown: true}")]
    [InlineData("version: 1\ncompatibility: {fail_on: [], fail_on_unknown: true}")]
    [InlineData("version: 1\ncompatibility: {fail_on: ERR, fail_on_unknown: true}")]
    [InlineData("version: 1\ncompatibility: {fail_on: [UNKNOWN], fail_on_unknown: true}")]
    [InlineData("version: 1\ncompatibility: {fail_on: [ERR], fail_on_unknown: 'false'}")]
    [InlineData("version: 1\ncompatibility: {fail_on: [ERR], fail_on_unknown: true}\nrules: {other-rule: INFO}")]
    [InlineData("version: 1\ncompatibility: {fail_on: [ERR], fail_on_unknown: true}\n---\nversion: 1")]
    public void RejectsInvalidPolicy(string yaml)
    {
        Assert.Throws<PolicyException>(() => Parse(yaml));
    }

    private static CompatibilityPolicy Parse(string yaml) => PolicyFile.Parse(Encoding.UTF8.GetBytes(yaml)).Policy;

    private static ContractFinding Finding(FindingSeverity severity) =>
        new("response-property-type-changed", severity, "GET", "/runs", null, null, null, "Changed", "same");
}
