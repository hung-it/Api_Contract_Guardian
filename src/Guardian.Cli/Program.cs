using System.Reflection;
using Guardian.Cli;
using Guardian.Core;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine(
        """
        Usage: guardian --help | version
               guardian validate <spec> [--input-root <dir>]
               guardian compare --base <spec> --candidate <spec> [--policy <file>] [--input-root <dir>] [--out <dir>]
               guardian publish --report <file> --api-url <url> --project <uuid>

        Commands:
          version  Print the Guardian version.
          validate Check a local OpenAPI spec.
          compare  Analyze compatibility between two local OpenAPI specs.
          publish  Send a report to a Guardian API using GUARDIAN_REPORT_TOKEN.
        """);
    return (int)GateExitCode.Success;
}

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
    .InformationalVersion.Split('+', 2)[0];
if (args is ["version"])
{
    Console.WriteLine(version);
    return (int)GateExitCode.Success;
}

if (args[0] == "publish")
{
    return await ReportPublisher.RunAsync(args[1..]);
}

var (basePath, candidatePath, policyPath, outputDirectory, inputRoot) = GetPaths(args);
if (basePath is not null)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancel;
    SpecDocument? spec = null;
    SpecDocument? candidate = null;
    PolicyFile? policy = null;
    PolicyDecision? decision = null;
    ReportDiagnostic? diagnostic = null;
    var exit = GateExitCode.Success;
    try
    {
        policy = candidatePath is null ? null : PolicyFile.Load(policyPath, inputRoot);
        spec = LoadSpec(basePath, candidatePath is null ? "Spec" : "Baseline", inputRoot);
        candidate = candidatePath is null ? null : LoadSpec(candidatePath, "Candidate", inputRoot);
        var engine = new OasdiffAdapter(Environment.GetEnvironmentVariable("GUARDIAN_OASDIFF_PATH"));
        var output = await engine.AnalyzeAsync(spec, candidate, cancellation.Token);
        if (output is null)
        {
            Console.WriteLine("OpenAPI spec is valid.");
            return (int)GateExitCode.Success;
        }

        var requestBodyChanges = RequestBodyCompatibility.Compare(spec, candidate!);
        decision = policy!.Policy.Evaluate(FindingNormalizer.Parse(output, requestBodyChanges));
        exit = decision.Status == AnalysisStatus.PASS ? GateExitCode.Success : GateExitCode.PolicyFailure;
    }
    catch (SpecInputException error)
    {
        diagnostic = new("INVALID_SPEC", error.Message);
        exit = GateExitCode.InvalidSpec;
    }
    catch (EngineException error)
    {
        diagnostic = new("ENGINE_FAILURE", error.Message);
        exit = GateExitCode.EngineFailure;
    }
    catch (PolicyException error)
    {
        diagnostic = new("INVALID_POLICY", error.Message);
        exit = GateExitCode.InvalidPolicy;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }

    if (diagnostic is not null)
    {
        Console.Error.WriteLine(diagnostic.Message);
    }

    if (decision is not null || outputDirectory is not null)
    {
        var report = new AnalysisReport(decision?.Status ?? AnalysisStatus.INDETERMINATE, decision?.Findings ?? [],
            ReportProvenance.Create(version, policy, spec, candidate), diagnostic is null ? [] : [diagnostic]);
        var json = report.ToJson();
        if (outputDirectory is not null)
        {
            try
            {
                report.Write(outputDirectory, json);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine("Could not write the Guardian reports.");
                return (int)GateExitCode.EngineFailure;
            }
        }
        if (decision is not null)
        {
            Console.WriteLine(json);
        }
    }
    return (int)exit;
}

Console.Error.WriteLine("Invalid arguments. Run 'guardian --help' for usage.");
return 1;

static SpecDocument LoadSpec(string path, string role, string? inputRoot)
{
    try
    {
        return SpecLoader.Load(path, inputRoot);
    }
    catch (SpecInputException error)
    {
        throw new SpecInputException($"{role}: {error.Message}");
    }
}

static (string? Baseline, string? Candidate, string? Policy, string? Out, string? InputRoot) GetPaths(string[] arguments)
{
    static bool IsPath(string value) => !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("--", StringComparison.Ordinal);

    if (arguments is ["validate", var path] && IsPath(path))
    {
        return (path, null, null, null, null);
    }
    if (arguments is ["validate", var rootedPath, "--input-root", var root] && IsPath(rootedPath) && IsPath(root))
    {
        return (rootedPath, null, null, null, root);
    }
    if (arguments.Length < 5 || arguments.Length % 2 == 0 || arguments[0] != "compare")
    {
        return default;
    }

    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < arguments.Length; index += 2)
    {
        var option = arguments[index];
        var value = arguments[index + 1];
        if (option is not ("--base" or "--candidate" or "--policy" or "--out" or "--input-root") || !IsPath(value)
            || !options.TryAdd(option, value))
        {
            return default;
        }
    }
    return options.TryGetValue("--base", out var baseline) && options.TryGetValue("--candidate", out var candidate)
        ? (baseline, candidate, options.GetValueOrDefault("--policy"), options.GetValueOrDefault("--out"),
            options.GetValueOrDefault("--input-root"))
        : default;
}
