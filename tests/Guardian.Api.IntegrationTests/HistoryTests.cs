using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Guardian.Api;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Guardian.Api.IntegrationTests;

public sealed class HistoryTests : PostgresApi
{
    [Fact]
    public async Task RequiresCookieAndProjectMembershipForEachHistoryRoute()
    {
        using var created = await Publish(Fixture("flowforge-breaking"), Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("analysis_id").GetGuid();
        using var anonymous = await Client.GetAsync($"/api/v1/analyses/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await AddReviewer("outsider@example.invalid", ProjectRole.Viewer, OtherProjectId);
        await SignIn("outsider@example.invalid");
        using var list = await Client.GetAsync($"/api/v1/projects/{ProjectId}/analyses");
        using var detail = await Client.GetAsync($"/api/v1/analyses/{id}");
        using var findings = await Client.GetAsync($"/api/v1/analyses/{id}/findings");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, findings.StatusCode);
        var projects = await Client.GetFromJsonAsync<JsonElement>("/api/v1/projects");
        Assert.Equal(OtherProjectId, Assert.Single(projects.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ReturnsDetailAndCombinedFindingFilters()
    {
        using var created = await Publish(Fixture("flowforge-breaking"), Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("analysis_id").GetGuid();
        await AddReviewer("reader@example.invalid", ProjectRole.Developer);
        await SignIn("reader@example.invalid");
        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/analyses/{id}");
        Assert.Equal("FAIL", detail.GetProperty("status").GetString());
        Assert.Equal("3107066933", detail.GetProperty("provenance").GetProperty("pipeline_id").GetString());
        var matches = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/analyses/{id}/findings?severity=ERR&rule=response-property-type-changed");
        Assert.Equal("/api/v1/runs/{run_id}", Assert.Single(matches.GetProperty("items").EnumerateArray())
            .GetProperty("path").GetString());
        var empty = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/analyses/{id}/findings?severity=WARN&rule=response-property-type-changed");
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task CursorSeparatesAnalysesWithTheSameTimestamp()
    {
        foreach (var fixture in new[] { "flowforge-breaking", "flowforge-compatible" })
        {
            using var response = await Publish(Fixture(fixture), Token);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        await using var db = Database();
        var time = DateTimeOffset.UtcNow;
        await db.Analyses.ExecuteUpdateAsync(fields => fields.SetProperty(row => row.CreatedAt, time));
        await AddReviewer("reader@example.invalid", ProjectRole.Viewer);
        await SignIn("reader@example.invalid");
        var history = $"/api/v1/projects/{ProjectId}/analyses?limit=1";
        var first = await Client.GetFromJsonAsync<JsonElement>(history);
        var cursor = first.GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor);
        var second = await Client.GetFromJsonAsync<JsonElement>($"{history}&cursor={cursor}");
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("id").GetGuid(),
            second.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_cursor").ValueKind);
        using var invalid = await Client.GetAsync($"{history}&cursor=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task LoginRequiresAntiforgeryAndLogoutEndsSession()
    {
        const string email = "reader@example.invalid";
        await AddReviewer(email, ProjectRole.Viewer);
        var credentials = new { email, password = ReviewerPassword };
        using var unprotected = await Client.PostAsJsonAsync("/api/v1/auth/login", credentials);
        Assert.Equal(HttpStatusCode.BadRequest, unprotected.StatusCode);
        await SignIn(email);
        using var logout = await BrowserPost("/api/v1/auth/logout", HttpMethod.Post);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var projects = await Client.GetAsync("/api/v1/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, projects.StatusCode);
    }
}
