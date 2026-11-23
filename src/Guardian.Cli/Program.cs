using System.Reflection;
using Guardian.Cli;
using Guardian.Core;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine(
        """
        Usage: guardian [--help | version | validate <spec>]

        Commands:
          version  Print the Guardian version.
          validate Check a local OpenAPI spec.
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

if (args is ["validate", var path] && !path.StartsWith("--", StringComparison.Ordinal))
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
        var spec = SpecLoader.Load(path);
        var engine = new OasdiffAdapter(Environment.GetEnvironmentVariable("GUARDIAN_OASDIFF_PATH"));
        await engine.AnalyzeAsync(spec, null, cancellation.Token);
        Console.WriteLine("OpenAPI spec is valid.");
        return (int)GateExitCode.Success;
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
