using System.Text;
using Guardian.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Guardian.Cli;

internal sealed class PolicyException(string message) : Exception(message);

internal sealed record PolicyFile(CompatibilityPolicy Policy, byte[] Bytes)
{
    private const int MaximumBytes = 64 * 1024;
    private const string DefaultYaml = """
        version: 1
        compatibility:
          fail_on: [ERR]
          fail_on_unknown: true
        """;

    internal static PolicyFile Load(string? path)
    {
        if (path is null)
        {
            return Parse(Encoding.UTF8.GetBytes(DefaultYaml));
        }

        if (path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || (!Path.IsPathFullyQualified(path) && path.Contains(':')))
        {
            throw new PolicyException("Provide a local Guardian policy file.");
        }

        try
        {
            using var file = File.OpenRead(path);
            var length = file.Length;
            if (length > MaximumBytes)
            {
                throw new PolicyException("Policy exceeds the 64 KiB limit.");
            }
            var bytes = new byte[(int)length];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1)
            {
                throw new PolicyException("Policy changed during reading.");
            }
            return Parse(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw new PolicyException("Policy file could not be read.");
        }
    }

    internal static PolicyFile Parse(byte[] bytes)
    {
        try
        {
            var parser = new Parser(new StringReader(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF')));
            parser.Consume<StreamStart>();
            parser.Consume<DocumentStart>();
            parser.Consume<MappingStart>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var overrides = new Dictionary<string, FindingSeverity>(StringComparer.Ordinal);
            FindingSeverity? threshold = null;
            bool? failOnUnknown = null;
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                switch (ReadKey(parser, seen))
                {
                    case "version":
                        if (ReadScalar(parser, plain: true) != "1")
                        {
                            throw new PolicyException("Policy version must be 1.");
                        }
                        break;
                    case "compatibility":
                        (threshold, failOnUnknown) = ReadCompatibility(parser);
                        break;
                    case "rules":
                        ReadOverrides(parser, overrides);
                        break;
                    default:
                        throw new PolicyException("Unrecognized field in Guardian policy.");
                }
            }

            parser.Consume<DocumentEnd>();
            parser.Consume<StreamEnd>();
            if (!seen.Contains("version") || threshold is null || failOnUnknown is null)
            {
                throw new PolicyException("Policy requires version, fail_on and fail_on_unknown.");
            }
            return new PolicyFile(new CompatibilityPolicy(threshold.Value, failOnUnknown.Value, overrides), bytes);
        }
        catch (Exception error) when (error is YamlException or DecoderFallbackException)
        {
            throw new PolicyException("Policy must be a valid UTF-8 YAML document matching schema v1.");
        }
    }

    private static (FindingSeverity? Threshold, bool? FailOnUnknown) ReadCompatibility(IParser parser)
    {
        parser.Consume<MappingStart>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        FindingSeverity? threshold = null;
        bool? unknown = null;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            switch (ReadKey(parser, seen))
            {
                case "direction":
                    if (ReadScalar(parser) != "backward")
                    {
                        throw new PolicyException("Only backward compatibility is supported.");
                    }
                    break;
                case "fail_on_unknown":
                    unknown = ReadScalar(parser, plain: true) switch
                    {
                        "true" => true,
                        "false" => false,
                        _ => throw new PolicyException("fail_on_unknown must be true or false."),
                    };
                    break;
                case "fail_on":
                    parser.Consume<SequenceStart>();
                    while (!parser.TryConsume<SequenceEnd>(out _))
                    {
                        var severity = ReadSeverity(parser);
                        if (threshold is null || severity < threshold)
                        {
                            threshold = severity;
                        }
                    }
                    break;
                default:
                    throw new PolicyException("Unrecognized compatibility field in Guardian policy.");
            }
        }
        return (threshold, unknown);
    }

    private static void ReadOverrides(IParser parser, Dictionary<string, FindingSeverity> overrides)
    {
        parser.Consume<MappingStart>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var rule = ReadKey(parser, seen);
            if (!CompatibilityPolicy.IsKnownRule(rule))
            {
                throw new PolicyException("Policy override refers to an unrecognized rule.");
            }
            overrides.Add(rule, ReadSeverity(parser));
        }
    }

    private static FindingSeverity ReadSeverity(IParser parser) => ReadScalar(parser) switch
    {
        "INFO" => FindingSeverity.INFO,
        "WARN" => FindingSeverity.WARN,
        "ERR" => FindingSeverity.ERR,
        _ => throw new PolicyException("Policy severity must be INFO, WARN or ERR."),
    };

    private static string ReadKey(IParser parser, HashSet<string> seen)
    {
        var key = ReadScalar(parser);
        if (!seen.Add(key))
        {
            throw new PolicyException("Duplicate key in Guardian policy.");
        }
        return key;
    }

    private static string ReadScalar(IParser parser, bool plain = false)
    {
        var scalar = parser.Consume<Scalar>();
        if (!scalar.Tag.IsEmpty || (plain && scalar.Style != YamlDotNet.Core.ScalarStyle.Plain))
        {
            throw new PolicyException("Invalid scalar type in Guardian policy.");
        }
        return scalar.Value;
    }
}
