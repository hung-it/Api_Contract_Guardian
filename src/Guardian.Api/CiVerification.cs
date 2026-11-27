using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Guardian.Api;

internal sealed partial class CiVerification(HttpClient client, IConfiguration configuration)
{
    private const int MetadataLimit = 1024 * 1024;
    private const int ArchiveLimit = 24 * 1024 * 1024;
    private const int ManifestLimit = 16 * 1024;

    internal async Task<Evidence?> Verify(Analysis analysis, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var published = JsonDocument.Parse(analysis.Report);
        var provenance = published.RootElement.GetProperty("provenance");
        var source = Field(provenance, "ci_provider");
        var run = Field(provenance, "pipeline_id");
        var candidate = Field(provenance, "candidate_commit_sha");
        var baseline = Field(provenance, "baseline_commit_sha");
        if (source is not ("github" or "gitlab") || !IsId(run) ||
            !CommitPattern().IsMatch(candidate) || !CommitPattern().IsMatch(baseline))
        {
            return null;
        }

        var settings = configuration.GetSection($"Guardian:Ci:Projects:{analysis.ProjectId:D}:{source}");
        var digest = settings["ImageDigest"] ?? "";
        if (!DigestPattern().IsMatch(digest))
        {
            throw new HttpRequestException("CI verification is not configured.");
        }
        var verdict = analysis.Status;
        var archiveUrl = source == "github"
            ? await GitHubArtifact(settings, run, candidate, verdict, token)
            : await GitLabArtifact(settings, run, Field(provenance, "ci_job_id"), candidate, verdict, token);
        if (archiveUrl is null)
        {
            return null;
        }
        var prefix = source == "gitlab" ? "reports/guardian/" : "";
        using var zip = new ZipArchive(await Download(archiveUrl, settings, ArchiveLimit, token, allowDownloadRedirect: true));
        using var report = await ReadEntry(zip, prefix + "report.json", ReportIngestion.MaxReportBytes, token);
        using var evidence = await ReadEntry(zip, prefix + "evidence.json", ManifestLimit, token);
        using var manifest = JsonDocument.Parse(evidence.ToArray(), new() { AllowDuplicateProperties = false });
        var actual = manifest.RootElement;
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(report.ToArray()));
        if (Field(actual, "provider") != source || Field(actual, "run_id") != run ||
            Field(actual, "candidate_sha") != candidate || Field(actual, "baseline_sha") != baseline ||
            Field(actual, "image_digest") != digest || Field(actual, "verdict") != verdict ||
            Field(actual, "report_sha256") != hash)
        {
            return null;
        }
        using var artifactReport = JsonDocument.Parse(report.ToArray(), new() { AllowDuplicateProperties = false });
        return JsonElement.DeepEquals(published.RootElement, artifactReport.RootElement)
            ? new("VERIFIED", source, run, baseline, candidate, digest, hash, DateTimeOffset.UtcNow)
            : null;
    }

    private async Task<string?> GitHubArtifact(IConfiguration settings, string run, string candidate, string verdict,
        CancellationToken token)
    {
        var repository = settings["Repository"] ?? "";
        var app = settings["AppId"] ?? "";
        if (!RepositoryPattern().IsMatch(repository) || !IsId(app))
        {
            throw new HttpRequestException("GitHub verification is not configured.");
        }
        var root = $"https://api.github.com/repos/{repository}";
        var checkUrl = $"{root}/commits/{candidate}/check-runs?check_name=guardian-contract&app_id={app}&filter=all&per_page=100";
        using var checks = await ReadJson(checkUrl, settings, token);
        foreach (var check in checks.RootElement.GetProperty("check_runs").EnumerateArray())
        {
            if (Field(check, "name") != "guardian-contract" || Field(check, "head_sha") != candidate ||
                Number(check.GetProperty("app"), "id") != app || Field(check, "status") != "completed" ||
                Field(check, "conclusion") != (verdict == "PASS" ? "success" : "failure") ||
                Field(check, "external_id").Split(':') is not [var runId, var artifact] ||
                runId != run || !IsId(artifact))
            {
                continue;
            }
            var url = $"{root}/actions/artifacts/{artifact}";
            using var metadata = await ReadJson(url, settings, token);
            var value = metadata.RootElement;
            if (value.GetProperty("expired").ValueKind == JsonValueKind.False &&
                Number(value.GetProperty("workflow_run"), "id") == run)
            {
                return url + "/zip";
            }
        }
        return null;
    }

    private async Task<string?> GitLabArtifact(IConfiguration settings, string run, string job, string candidate,
        string verdict, CancellationToken token)
    {
        if (!IsId(job))
        {
            return null;
        }
        var project = settings["ProjectId"] ?? "";
        if (!IsId(project))
        {
            throw new HttpRequestException("GitLab verification is not configured.");
        }
        var url = $"https://gitlab.com/api/v4/projects/{project}/jobs/{job}";
        using var metadata = await ReadJson(url, settings, token);
        var value = metadata.RootElement;
        if (Number(value, "id") != job || Number(value.GetProperty("pipeline"), "id") != run ||
            Field(value.GetProperty("commit"), "id") != candidate || Field(value, "ref") != "main" ||
            Field(value, "name") != "guardian_release_evidence" ||
            Field(value, "status") != (verdict == "PASS" ? "success" : "failed"))
        {
            return null;
        }
        using var pipeline = await ReadJson($"https://gitlab.com/api/v4/projects/{project}/pipelines/{run}", settings, token);
        var invocation = pipeline.RootElement;
        return Number(invocation, "id") == run && Field(invocation, "sha") == candidate &&
            Field(invocation, "ref") == "main" && Field(invocation, "source") is "push" or "web"
            ? url + "/artifacts" : null;
    }

    private async Task<JsonDocument> ReadJson(string url, IConfiguration settings, CancellationToken token)
    {
        using var bytes = await Download(url, settings, MetadataLimit, token);
        return JsonDocument.Parse(bytes.ToArray(), new() { AllowDuplicateProperties = false });
    }

    private async Task<MemoryStream> Download(string url, IConfiguration settings, int limit, CancellationToken token,
        bool allowDownloadRedirect = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (settings["Token"] is { Length: > 0 } secret)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (allowDownloadRedirect && (int)response.StatusCode is 302 or 303 or 307 or 308 &&
            response.Headers.Location is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } location &&
            AllowedStorage(location.IdnHost))
        {
            using var download = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, token);
            return await ReadResponse(download, limit, token);
        }
        return await ReadResponse(response, limit, token);
    }

    private static async Task<MemoryStream> ReadResponse(HttpResponseMessage response, int limit, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException("The CI artifact exceeds its size limit.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await ReadBounded(stream, limit, token);
    }

    private static async Task<MemoryStream> ReadEntry(ZipArchive archive, string path, int limit, CancellationToken token)
    {
        var entries = archive.Entries.Where(entry => entry.FullName == path).Take(2).ToArray();
        if (entries is not [var entry] || entry.Length > limit)
        {
            throw new InvalidDataException("The CI artifact is missing the expected file or exceeds its size limit.");
        }
        await using var stream = entry.Open();
        return await ReadBounded(stream, limit, token);
    }

    private static async Task<MemoryStream> ReadBounded(Stream stream, int limit, CancellationToken token)
    {
        var bytes = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + read > limit)
                {
                    throw new InvalidDataException("The CI artifact exceeds its size limit.");
                }
                bytes.Write(buffer, 0, read);
            }
            bytes.Position = 0;
            return bytes;
        }
        catch
        {
            bytes.Dispose();
            throw;
        }
    }

    private static bool AllowedStorage(string host) =>
        host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal) ||
        host.EndsWith(".actions.githubusercontent.com", StringComparison.Ordinal) ||
        host.EndsWith(".gitlab-static.net", StringComparison.Ordinal) ||
        host == "storage.googleapis.com";

    private static bool IsId(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
        id > 0 && id.ToString(CultureInfo.InvariantCulture) == value;

    private static string Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString()! : "";

    private static string Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.Number ? field.GetRawText() : "";

    [GeneratedRegex("\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_][A-Za-z0-9_.-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    internal sealed record Evidence(string Status, string Provider, string RunId, string BaselineSha,
        string CandidateSha, string ImageDigest, string ReportSha256, DateTimeOffset VerifiedAt);
}
