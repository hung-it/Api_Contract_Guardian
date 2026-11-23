using System.Text;
using Guardian.Cli;
using Xunit;

namespace Guardian.Core.Tests;

public sealed class FindingNormalizerTests
{
    private const string Breaking = """
        {"id":"response-property-type-changed","level":3,"operation":"get","path":"/Runs",
         "section":"paths","text":"Type changed","fingerprint":"52ac1104b525",
         "baseSource":{"file":"/private/snapshot.json","line":10,"column":2}}
        """;

    [Fact]
    public void NormalizesUpstreamRuleAndSourceLocation()
    {
        var finding = Assert.Single(Parse($"[{Breaking}]"));

        Assert.Equal("response-property-type-changed", finding.RuleId);
        Assert.Equal(FindingSeverity.ERR, finding.Severity);
        Assert.Equal("GET", finding.Method);
        Assert.Equal("/Runs", finding.Path);
        Assert.Equal("paths", finding.Section);
        Assert.Equal(new FindingLocation(10, 2), finding.BaseSource);
        Assert.Null(finding.CandidateSource);
        Assert.Matches("^sha256:[0-9a-f]{64}$", finding.Fingerprint);
    }

    [Fact]
    public void KeepsFingerprintWhenDescriptionOrPositionChanges()
    {
        var original = Assert.Single(Parse($"[{Breaking}]"));
        var moved = Breaking.Replace("Type changed", "Response field has a new type", StringComparison.Ordinal)
            .Replace("\"line\":10", "\"line\":25", StringComparison.Ordinal);
        var reordered = """
            {"fingerprint":"52ac1104b525","path":"/Runs","section":"paths","operation":"GET",
            "level":3,"text":"Different description","id":"response-property-type-changed"}
            """;

        Assert.Equal(original.Fingerprint, Assert.Single(Parse($"[{moved}]")).Fingerprint);
        Assert.Equal(original.Fingerprint, Assert.Single(Parse($"[{reordered}]")).Fingerprint);
    }

    [Fact]
    public void DifferentLogicalChangesHaveDifferentFingerprints()
    {
        var different = Breaking.Replace("52ac1104b525", "52ac1104b526", StringComparison.Ordinal);
        var findings = Parse($"[{Breaking},{different}]");

        Assert.NotEqual(findings[0].Fingerprint, findings[1].Fingerprint);
    }

    [Fact]
    public void PreservesUnknownIdsAndSortsIndependentlyOfInputOrder()
    {
        const string info = """
            {"id":"endpoint-added","level":1,"operation":"GET","path":"/health",
             "text":"Endpoint added","fingerprint":"ab75cd6a6d24"}
            """;
        const string unknown = """
            {"id":"unmapped-rule","level":2,"text":"Needs review","fingerprint":"abcd56780000"}
            """;
        var findings = Parse($"[{info},{Breaking},{unknown}]");

        Assert.Equal(findings, Parse($"[{unknown},{info},{Breaking}]"));
        Assert.Equal(new[] { FindingSeverity.UNKNOWN, FindingSeverity.ERR, FindingSeverity.INFO },
            findings.Select(finding => finding.Severity));
        Assert.Equal("unmapped-rule", findings[0].RuleId);
        Assert.Null(findings[0].Method);
    }

    [Fact]
    public void EmptyArrayHasNoFindings()
    {
        Assert.Empty(Parse("[]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[null]")]
    [InlineData("""[{"id":"endpoint-added","level":4,"text":"Added","fingerprint":"ab75cd6a6d24"}]""")]
    [InlineData("""[{"id":"endpoint-added","level":"1","text":"Added","fingerprint":"ab75cd6a6d24"}]""")]
    [InlineData("""[{"id":"endpoint-added","level":1,"id":"other","text":"Added","fingerprint":"ab75cd6a6d24"}]""")]
    [InlineData("""[{"id":"endpoint-added","level":1,"text":"Added","fingerprint":"missing"}]""")]
    public void RejectsInvalidEngineOutput(string json)
    {
        Assert.Throws<EngineException>(() => Parse(json));
    }

    private static ContractFinding[] Parse(string json) => FindingNormalizer.Parse(Encoding.UTF8.GetBytes(json));
}
