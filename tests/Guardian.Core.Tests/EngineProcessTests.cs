using System.Text.Json;
using Guardian.Cli;
using Xunit;

namespace Guardian.Core.Tests;

public sealed class EngineProcessTests
{
    private static readonly string TestEngine = Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "Guardian.TestEngine.exe" : "Guardian.TestEngine");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PreservesArgumentsAndDrainsBothStreams()
    {
        string[] values = ["first path/file with spaces.yaml", "second path/other file.yaml"];
        var result = await EngineProcess.RunAsync(TestEngine, AppContext.BaseDirectory,
            ["echo", .. values], CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(values, JsonSerializer.Deserialize<string[]>(result.Stdout));
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task StopsWhenEitherStreamExceedsItsLimit(string stream)
    {
        var error = await Assert.ThrowsAsync<EngineException>(() => EngineProcess.RunAsync(
            TestEngine, AppContext.BaseDirectory, [stream], CancellationToken.None,
            timeout: TestTimeout, stdoutLimit: 512, stderrLimit: 512));

        Assert.Contains(stream, error.Message);
    }

    [Fact]
    public async Task StopsAfterTimeout()
    {
        var error = await Assert.ThrowsAsync<EngineException>(() => EngineProcess.RunAsync(
            TestEngine, AppContext.BaseDirectory, ["hold"], CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Contains("timed out", error.Message);
    }

    [Fact]
    public async Task StopsOnCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsAsync<EngineException>(() => EngineProcess.RunAsync(
            TestEngine, AppContext.BaseDirectory, ["hold"], cancellation.Token));

        Assert.Contains("canceled", error.Message);
    }

    [Theory]
    [InlineData("load-error")]
    [InlineData("validation-error")]
    public async Task MapsKnownValidationFailuresToSpecError(string marker)
    {
        var adapter = new OasdiffAdapter(TestEngine);

        await Assert.ThrowsAsync<SpecInputException>(() => adapter.AnalyzeAsync(
            TestDocument(marker), null, CancellationToken.None));
    }

    [Theory]
    [InlineData("broken-output")]
    [InlineData("invalid-output-string")]
    public async Task RejectsMalformedValidationOutput(string marker)
    {
        var adapter = new OasdiffAdapter(TestEngine);

        await Assert.ThrowsAsync<EngineException>(() => adapter.AnalyzeAsync(
            TestDocument(marker), null, CancellationToken.None));
    }

    [Fact]
    public async Task UsesIsolatedConfigAndSnapshotsForComparison()
    {
        var adapter = new OasdiffAdapter(TestEngine);

        var result = await adapter.AnalyzeAsync(TestDocument("base"), TestDocument("compare"),
            CancellationToken.None);

        Assert.Equal("[]"u8.ToArray(), result);
    }

    [Fact]
    public async Task RequiresEngineAtAnExplicitAbsolutePath()
    {
        var adapter = new OasdiffAdapter("oasdiff");

        await Assert.ThrowsAsync<EngineException>(() => adapter.AnalyzeAsync(
            TestDocument("example"), null, CancellationToken.None));
    }

    private static SpecDocument TestDocument(string marker) => new(".yaml", "3.1",
        System.Text.Encoding.UTF8.GetBytes($"openapi: 3.1.0\nx-test: {marker}"));
}
