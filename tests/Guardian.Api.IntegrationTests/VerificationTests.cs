using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardian.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Guardian.Api.IntegrationTests;

public sealed class VerificationTests : PostgresApi
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Baseline = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Digest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private readonly CiFixture _ci = new();

    [Theory]
    [InlineData("github", "flowforge-compatible", "PASS")]
    [InlineData("gitlab", "flowforge-breaking", "FAIL")]
    public async Task TrustedArtifactIsVerifiedOnce(string provider, string fixture, string verdict)
    {
        var body = Report(fixture, provider);
        _ci.Verdict = verdict;
        _ci.Archive = Artifact(provider, body, verdict);
        using var created = await Publish(body, Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("analysis_id").GetGuid();
        var detailUrl = $"/api/v1/analyses/{id}";
        var endpoint = detailUrl + "/verification";

        await AddReviewer("viewer@example.invalid", ProjectRole.Viewer);
        await SignIn("viewer@example.invalid");
        using var forbidden = await BrowserPost(endpoint, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0, _ci.Requests);
        var detail = await Client.GetFromJsonAsync<JsonElement>(detailUrl);
        Assert.Equal("UNVERIFIED", detail.GetProperty("verification").GetProperty("status").GetString());

        await AddReviewer("maintainer@example.invalid", ProjectRole.Maintainer);
        await SignIn("maintainer@example.invalid");
        using var noCsrf = await Client.PostAsync(endpoint, null);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var verified = await BrowserPost(endpoint, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var evidence = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VERIFIED", evidence.GetProperty("status").GetString());
        Assert.Equal(provider, evidence.GetProperty("provider").GetString());
        Assert.Equal(Digest, evidence.GetProperty("image_digest").GetString());
        var requests = _ci.Requests;
        using var replay = await BrowserPost(endpoint, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(requests, _ci.Requests);
        detail = await Client.GetFromJsonAsync<JsonElement>(detailUrl);
        Assert.Equal(verdict, detail.GetProperty("status").GetString());
        Assert.True(JsonElement.DeepEquals(evidence, detail.GetProperty("verification")));
        await using var db = Database();
        Assert.Equal(1, await db.AuditEvents.CountAsync(row => row.Action == "analysis.verified" && row.EntityId == id));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("expired")]
    [InlineData("report")]
    public async Task UntrustedEvidenceDoesNotChangePublishedReport(string failure)
    {
        var body = Report("flowforge-compatible", "github");
        _ci.AppId = failure == "app" ? 99 : 42;
        _ci.Expired = failure == "expired";
        _ci.Archive = Artifact("github", failure == "report" ? Report("flowforge-breaking", "github") : body, "PASS");
        using var created = await Publish(body, Token);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("analysis_id").GetGuid();
        await AddReviewer("maintainer@example.invalid", ProjectRole.Maintainer);
        await SignIn("maintainer@example.invalid");
        using var response = await BrowserPost($"/api/v1/analyses/{id}/verification", HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = Database();
        Assert.Null(await db.Analyses.Where(row => row.Id == id).Select(row => row.Verification).SingleAsync());
        Assert.False(await db.AuditEvents.AnyAsync(row => row.Action == "analysis.verified"));
    }

    protected override void Configure(IWebHostBuilder builder)
    {
        var project = $"Guardian:Ci:Projects:{ProjectId:D}";
        builder.ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{project}:github:Repository"] = "example/FlowForge",
            [$"{project}:github:AppId"] = "42",
            [$"{project}:github:ImageDigest"] = Digest,
            [$"{project}:gitlab:ProjectId"] = "43",
            [$"{project}:gitlab:ImageDigest"] = Digest,
        }));
        builder.ConfigureTestServices(services => services.AddHttpClient("CiVerification")
            .ConfigurePrimaryHttpMessageHandler(() => _ci));
    }

    private static string Report(string fixture, string provider)
    {
        var body = JsonNode.Parse(Fixture(fixture))!;
        var claims = body["provenance"]!;
        claims["ci_provider"] = provider;
        claims["pipeline_id"] = "101";
        claims["baseline_commit_sha"] = Baseline;
        claims["candidate_commit_sha"] = Candidate;
        if (provider == "gitlab")
        {
            claims["ci_job_id"] = "102";
        }
        return body.ToJsonString();
    }

    private static byte[] Artifact(string provider, string report, string verdict)
    {
        using var bytes = new MemoryStream();
        var reportBytes = Encoding.UTF8.GetBytes(report);
        var prefix = provider == "gitlab" ? "reports/guardian/" : "";
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry(prefix + "report.json").Open())
            {
                entry.Write(reportBytes);
            }
            using var manifest = zip.CreateEntry(prefix + "evidence.json").Open();
            JsonSerializer.Serialize(manifest, new
            {
                provider,
                run_id = "101",
                candidate_sha = Candidate,
                baseline_sha = Baseline,
                image_digest = Digest,
                verdict,
                report_sha256 = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(reportBytes)),
            });
        }
        return bytes.ToArray();
    }

    private sealed class CiFixture : HttpMessageHandler
    {
        internal int AppId { get; set; } = 42;
        internal bool Expired { get; set; }
        internal string Verdict { get; set; } = "PASS";
        internal byte[] Archive { get; set; } = [];
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            var passing = Verdict == "PASS";
            HttpContent content = path switch
            {
                var value when value.EndsWith("/check-runs", StringComparison.Ordinal) =>
                    JsonContent.Create(new
                    {
                        check_runs = new[]
                        {
                            new { name = "guardian-contract", head_sha = Candidate, app = new { id = AppId },
                                status = "completed", conclusion = passing ? "success" : "failure", external_id = "101:103" },
                        },
                    }),
                "/repos/example/FlowForge/actions/artifacts/103" =>
                    JsonContent.Create(new { expired = Expired, workflow_run = new { id = 101 } }),
                "/api/v4/projects/43/jobs/102" => JsonContent.Create(new
                {
                    id = 102,
                    pipeline = new { id = 101 },
                    commit = new { id = Candidate },
                    @ref = "main",
                    name = "guardian_release_evidence",
                    status = passing ? "success" : "failed",
                }),
                "/api/v4/projects/43/pipelines/101" =>
                    JsonContent.Create(new { id = 101, sha = Candidate, @ref = "main", source = "web" }),
                "/repos/example/FlowForge/actions/artifacts/103/zip" or "/api/v4/projects/43/jobs/102/artifacts" =>
                    new ByteArrayContent(Archive),
                _ => throw new InvalidOperationException($"Unexpected provider request: {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
