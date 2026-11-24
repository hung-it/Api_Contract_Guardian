using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Guardian.Api.IntegrationTests;

public sealed class PublishTests : PostgresApi
{
    [Theory]
    [InlineData("flowforge-breaking", "FAIL", 1)]
    [InlineData("flowforge-compatible", "PASS", 0)]
    public async Task StoresWeekFiveReportAndProvenance(string file, string status, int count)
    {
        var raw = Fixture(file);
        using var response = await Publish(raw, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var expected = JsonDocument.Parse(raw);
        await using var db = Database();
        var row = await db.Analyses.Include(value => value.Findings).SingleAsync();
        Assert.Equal(await Id(response), row.Id);
        Assert.Equal(status, row.Status);
        Assert.Equal(count, row.Findings.Count);
        using var actual = JsonDocument.Parse(row.Report);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    [Fact]
    public async Task ConcurrentRetriesStoreOneAnalysisAndOneFinding()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Publish(Fixture("flowforge-breaking"), Token)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(11, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Single((await Task.WhenAll(responses.Select(Id))).Distinct());
            await using var db = Database();
            Assert.Equal(1, await db.Analyses.CountAsync());
            Assert.Equal(1, await db.Findings.CountAsync());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task RetryIgnoresJsonFormattingAndRejectsChangedContent()
    {
        var raw = Fixture("flowforge-breaking");
        using var first = await Publish(raw, Token);
        var report = JsonNode.Parse(raw)!.AsObject();
        var reordered = new JsonObject(report.Reverse().Select(field =>
            KeyValuePair.Create(field.Key, field.Value?.DeepClone())));
        using var retry = await Publish(reordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await Id(first), await Id(retry));
        report["findings"]![0]!["message"] = "A different compatibility finding.";
        using var conflict = await Publish(report.ToJsonString(), Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task ScopedTokensRejectOtherProjectsScopesAndExpiredCredentials()
    {
        var raw = Fixture("flowforge-compatible");
        const string expired = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string readOnly = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        await using var db = Database();
        db.Tokens.AddRange(CreateToken(expired, ProjectId, "reports:write", DateTimeOffset.UtcNow.AddDays(-1)),
            CreateToken(readOnly, ProjectId, "reports:read", DateTimeOffset.UtcNow.AddDays(1)));
        await db.SaveChangesAsync();
        using var absent = await Publish(raw, null);
        using var wrong = await Publish(raw, "invalid");
        using var timeout = await Publish(raw, expired);
        using var scope = await Publish(raw, readOnly);
        using var project = await Publish(raw, Token, OtherProjectId);
        Assert.Equal(HttpStatusCode.Unauthorized, absent.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, timeout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, scope.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, project.StatusCode);
        Assert.Empty(await db.Analyses.ToListAsync());
    }

    [Theory]
    [InlineData("{", HttpStatusCode.BadRequest)]
    [InlineData("{}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("{\"status\":\"PASS\",\"status\":\"FAIL\"}", HttpStatusCode.BadRequest)]
    public async Task RejectsInvalidPayloads(string body, HttpStatusCode status)
    {
        using var response = await Publish(body, Token);
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task RejectsOversizedReport()
    {
        using var response = await Publish(new string(' ', 10 * 1024 * 1024 + 1), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task FindingFailureRollsBackAnalysisAndRetryCanWrite()
    {
        var good = Fixture("flowforge-breaking");
        var report = JsonNode.Parse(good)!;
        report["findings"]![0]!["rule_id"] = "test-rejected";
        await using var db = Database();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE findings ADD CONSTRAINT test_rejected_rule CHECK (rule_id <> 'test-rejected')");
        using var rejected = await Publish(report.ToJsonString(), Token);
        Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
        Assert.Equal(0, await db.Analyses.CountAsync());
        using var retried = await Publish(good, Token);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }

    private static async Task<Guid> Id(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("analysis_id").GetGuid();
    }
}
