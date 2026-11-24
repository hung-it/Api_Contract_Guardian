using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Guardian.Api;
using Xunit;

namespace Guardian.Api.IntegrationTests;

public sealed class TokenTests : PostgresApi
{
    [Fact]
    public async Task ReadinessDetectsUnavailableDatabaseWhileLivenessStaysUp()
    {
        await using var factory = WithMissingDatabase();
        using var unhealthy = factory.CreateClient();
        using var ready = await Client.GetAsync("/health/ready");
        using var unavailable = await unhealthy.GetAsync("/health/ready");
        using var live = await unhealthy.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task MaintainerCanRotateTokenAndAuditRecordsEachChangeOnce()
    {
        const string email = "maintainer@example.invalid";
        var maintainer = await AddReviewer(email, ProjectRole.Maintainer);
        await SignIn(email);
        var project = $"/api/v1/projects/{ProjectId}";
        var tokens = $"{project}/tokens";

        using var issue = await BrowserPost(tokens, HttpMethod.Post, new { expires_in_days = 2 });
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        Assert.True(issue.Headers.CacheControl?.NoStore);
        var issued = await issue.Content.ReadFromJsonAsync<JsonElement>();
        var tokenId = issued.GetProperty("id").GetGuid();
        var secret = issued.GetProperty("token").GetString()!;
        Assert.Equal(64, secret.Length);
        var listing = await Client.GetFromJsonAsync<JsonElement>(tokens);
        var listed = Assert.Single(listing.EnumerateArray(), row => row.GetProperty("id").GetGuid() == tokenId);
        Assert.False(listed.TryGetProperty("token", out _));
        Assert.False(listed.TryGetProperty("token_hash", out _));

        var report = Fixture("flowforge-breaking");
        using var first = await Publish(report, secret);
        using var retry = await Publish(report, secret);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var analysisId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("analysis_id").GetGuid();

        var target = $"{tokens}/{tokenId}";
        using var unprotected = await Client.DeleteAsync(target);
        Assert.Equal(HttpStatusCode.BadRequest, unprotected.StatusCode);
        using var revoke = await BrowserPost(target, HttpMethod.Delete);
        using var revokeAgain = await BrowserPost(target, HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revokeAgain.StatusCode);
        using var revokedPublish = await Publish(report, secret);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedPublish.StatusCode);

        var audit = await Client.GetFromJsonAsync<JsonElement>($"{project}/audit-events");
        Assert.DoesNotContain(secret, audit.GetRawText(), StringComparison.Ordinal);
        var events = audit.EnumerateArray().ToDictionary(row => row.GetProperty("action").GetString()!);
        Assert.Equal(3, events.Count);
        Assert.All(events.Values, row => Assert.Equal(ProjectId, row.GetProperty("project_id").GetGuid()));
        Assert.Equal(maintainer, events["token.created"].GetProperty("actor_id").GetGuid());
        Assert.Equal(maintainer, events["token.revoked"].GetProperty("actor_id").GetGuid());
        Assert.Equal(tokenId, events["token.revoked"].GetProperty("entity_id").GetGuid());
        Assert.Equal(tokenId, events["analysis.published"].GetProperty("actor_id").GetGuid());
        Assert.Equal(analysisId, events["analysis.published"].GetProperty("entity_id").GetGuid());
    }

    [Fact]
    public async Task ViewerAndOtherProjectMaintainerCannotManageProjectTokens()
    {
        const string email = "viewer@example.invalid";
        await AddReviewer(email, ProjectRole.Viewer);
        await AddReviewer("outsider@example.invalid", ProjectRole.Maintainer, OtherProjectId);
        var project = $"/api/v1/projects/{ProjectId}";
        await SignIn(email);
        using var tokens = await Client.GetAsync($"{project}/tokens");
        using var create = await BrowserPost($"{project}/tokens", HttpMethod.Post, new { expires_in_days = 1 });
        using var audit = await Client.GetAsync($"{project}/audit-events");
        Assert.Equal(HttpStatusCode.Forbidden, tokens.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
        await SignIn("outsider@example.invalid");
        using var outside = await Client.GetAsync($"{project}/tokens");
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }
}
