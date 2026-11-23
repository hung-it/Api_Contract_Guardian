using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Guardian.Cli;

internal sealed class OasdiffAdapter(string? executable)
{
    internal const string Version = "1.38.0";

    internal async Task<byte[]?> AnalyzeAsync(SpecDocument baseline, SpecDocument? candidate,
        CancellationToken cancellationToken)
    {
        if (candidate is not null && baseline.Dialect != candidate.Dialect)
        {
            throw new SpecInputException("Compare specs with the same OpenAPI major and minor version.");
        }
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new EngineException("Set GUARDIAN_OASDIFF_PATH to the absolute path of the installed oasdiff binary.");
        }

        DirectoryInfo directory;
        try
        {
            directory = Directory.CreateTempSubdirectory("guardian-analysis-");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new EngineException("Could not create the isolated analysis directory.");
        }
        var configuration = Path.Combine(directory.FullName, "oasdiff.yml");
        var basePath = Path.Combine(directory.FullName, "baseline" + baseline.Extension);
        var candidatePath = Path.Combine(directory.FullName, "candidate" + candidate?.Extension);
        try
        {
            var version = await RunAsync(["--version"]);
            if (version.ExitCode != 0 || Encoding.UTF8.GetString(version.Stdout).Trim() != $"oasdiff version {Version}")
            {
                throw new EngineException($"Expected oasdiff version {Version}.");
            }

            File.WriteAllText(configuration, "{}");
            File.WriteAllBytes(basePath, baseline.Bytes);
            await ValidateAsync(basePath, candidate is null ? "Spec" : "Baseline");
            if (candidate is null)
            {
                return null;
            }

            File.WriteAllBytes(candidatePath, candidate.Bytes);
            await ValidateAsync(candidatePath, "Candidate");
            var diff = await RunAsync(["--config", configuration, "changelog", "--allow-external-refs=false",
                "--format", "json", basePath, candidatePath]);
            if (diff.ExitCode != 0)
            {
                throw new EngineException($"oasdiff changelog exited with code {diff.ExitCode}.");
            }
            return diff.Stdout;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new EngineException("Could not prepare the isolated spec snapshots.");
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(candidatePath);
            File.Delete(configuration);
            directory.Delete();
        }

        Task<EngineResult> RunAsync(string[] arguments) =>
            EngineProcess.RunAsync(executable, directory.FullName, arguments, cancellationToken);

        async Task ValidateAsync(string path, string label)
        {
            var result = await RunAsync(["--config", configuration, "validate", "--allow-external-refs=false",
                "--fail-on", "ERR", "--format", "json", path]);
            if (result.ExitCode == 102)
            {
                throw new SpecInputException($"{label} could not be loaded by oasdiff.");
            }
            if (result.ExitCode is not (0 or 1))
            {
                throw new EngineException($"oasdiff validate exited with code {result.ExitCode}.");
            }

            var errorRule = ReadValidationError(result.Stdout);
            if (result.ExitCode != (errorRule is null ? 0 : 1))
            {
                throw new EngineException("oasdiff validation status disagrees with its JSON output.");
            }
            if (errorRule is not null)
            {
                throw new SpecInputException($"{label} failed OpenAPI validation ({errorRule}).");
            }
        }
    }

    private static string? ReadValidationError(byte[] output)
    {
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new EngineException("oasdiff returned invalid validation JSON.");
            }

            string? errorRule = null;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("level", out var level)
                    || level.ValueKind != JsonValueKind.Number || !level.TryGetInt32(out var severity)
                    || severity is < 1 or > 3
                    || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !Regex.IsMatch(id.GetString()!, @"\A[a-z0-9-]{1,128}\z"))
                {
                    throw new EngineException("oasdiff returned invalid validation JSON.");
                }
                if (severity == 3)
                {
                    errorRule ??= id.GetString();
                }
            }

            return errorRule;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new EngineException("oasdiff returned invalid validation JSON.");
        }
    }
}
