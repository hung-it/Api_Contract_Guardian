using System.Text.Json;

switch (args)
{
    case ["--version"]:
        Console.WriteLine("oasdiff version 1.38.0");
        return 0;
    case ["echo", .. var values]:
        Console.WriteLine(JsonSerializer.Serialize(values));
        Console.Error.WriteLine("test diagnostic");
        return 0;
    case ["stdout"]:
        Console.Write(new string('a', 4096));
        break;
    case ["stderr"]:
        Console.Error.Write(new string('a', 4096));
        break;
    case ["hold"]:
        break;
    case ["--config", var config, "validate", ..]:
        if (!File.Exists(config) || !args.Contains("--allow-external-refs=false"))
        {
            return 8;
        }

        var spec = File.ReadAllText(args[^1]);
        if (spec.Contains("load-error", StringComparison.Ordinal))
        {
            return 102;
        }
        if (spec.Contains("validation-error", StringComparison.Ordinal))
        {
            Console.Write("""[{"id":"schema-type-unsupported","level":3}]""");
            return 1;
        }
        if (spec.Contains("broken-output", StringComparison.Ordinal))
        {
            Console.Write("invalid JSON");
            return 0;
        }
        if (spec.Contains("invalid-output-string", StringComparison.Ordinal))
        {
            Console.Write("""[{"id":"\uD800","level":1}]""");
            return 0;
        }

        Console.Write("[]");
        return 0;
    case ["--config", _, "changelog", ..]:
        if (!args.Contains("--allow-external-refs=false") || args.Contains("--fail-on"))
        {
            return 8;
        }

        var candidate = File.ReadAllText(args[^1]);
        Console.Write(candidate.Contains("malformed-changelog", StringComparison.Ordinal)
            ? "incomplete JSON"
            : candidate.Contains("unmapped-changelog", StringComparison.Ordinal)
                ? """[{"id":"unmapped-rule","level":2,"text":"Needs review","fingerprint":"abcd56780000"}]"""
                : "[]");
        return 0;
    default:
        return 8;
}

await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
