using System.Reflection;
using Guardian.Core;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine(
        """
        Usage: guardian [--help | version]

        Commands:
          version  Print the Guardian version.
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

Console.Error.WriteLine("Invalid arguments. Run 'guardian --help' for usage.");
return 1;
