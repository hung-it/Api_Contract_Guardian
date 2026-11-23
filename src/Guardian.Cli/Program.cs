using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Guardian.Cli;
using Guardian.Core;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine(
        """
        Usage: guardian [--help | version | validate <spec> | compare --base <spec> --candidate <spec>]

        Commands:
          version  Print the Guardian version.
          validate Check a local OpenAPI spec.
          compare  Analyze compatibility between two local OpenAPI specs.
        """);
    return (int)GateExitCode.Success;
}

if (args is ["version"])
{
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion;
    Console.WriteLine(version.Split('+', 2)[0]);
    return (int)GateExitCode.Success;
}

var (basePath, candidatePath) = GetPaths(args);
if (basePath is not null)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        var spec = SpecLoader.Load(basePath);
        var candidate = candidatePath is null ? null : SpecLoader.Load(candidatePath);
        var engine = new OasdiffAdapter(Environment.GetEnvironmentVariable("GUARDIAN_OASDIFF_PATH"));
        var output = await engine.AnalyzeAsync(spec, candidate, cancellation.Token);
        if (output is null)
        {
            Console.WriteLine("OpenAPI spec is valid.");
            return (int)GateExitCode.Success;
        }

        var findings = FindingNormalizer.Parse(output);
        var json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter<FindingSeverity>() },
        };
        Console.WriteLine(JsonSerializer.Serialize(new { Engine = "oasdiff", EngineVersion = OasdiffAdapter.Version, Findings = findings }, json));
        return findings.Any(finding => finding.Severity is FindingSeverity.ERR or FindingSeverity.UNKNOWN)
            ? (int)GateExitCode.PolicyFailure
            : (int)GateExitCode.Success;
    }
    catch (SpecInputException error)
    {
        Console.Error.WriteLine(error.Message);
        return (int)GateExitCode.InvalidSpec;
    }
    catch (EngineException error)
    {
        Console.Error.WriteLine(error.Message);
        return (int)GateExitCode.EngineFailure;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}

Console.Error.WriteLine("Invalid arguments. Run 'guardian --help' for usage.");
return 1;

static (string? Baseline, string? Candidate) GetPaths(string[] arguments)
{
    static bool IsPath(string value) => !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("--", StringComparison.Ordinal);

    return arguments switch
    {
        ["validate", var path] when IsPath(path) => (path, null),
        ["compare", "--base", var baseline, "--candidate", var candidate]
            when IsPath(baseline) && IsPath(candidate) => (baseline, candidate),
        ["compare", "--candidate", var candidate, "--base", var baseline]
            when IsPath(baseline) && IsPath(candidate) => (baseline, candidate),
        _ => (null, null),
    };
}
