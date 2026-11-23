using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Guardian.Cli;

internal sealed record SpecDocument(string Extension, string Dialect, byte[] Bytes);

internal sealed class SpecInputException(string message) : Exception(message);

internal static class SpecLoader
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private const int MaximumDepth = 64;
    private const int MaximumReferences = 2048;
    private const string DefaultDialect = "https://spec.openapis.org/oas/3.1/dialect/base";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static SpecDocument Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || (!Path.IsPathFullyQualified(path) && path.Contains(':')))
        {
            throw new SpecInputException("Provide a local OpenAPI file path.");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".json" or ".yaml" or ".yml"))
        {
            throw new SpecInputException("Use a local .json, .yaml, or .yml file.");
        }

        byte[] bytes;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()
                && (fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                    || new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType == DriveType.Network))
            {
                throw new SpecInputException("Provide a local OpenAPI file path.");
            }

            if ((File.GetAttributes(fullPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new SpecInputException("Provide a regular OpenAPI file.");
            }

            using var file = File.OpenRead(fullPath);
            var length = file.Length;
            if (length > MaximumBytes)
            {
                throw new SpecInputException("Spec exceeds the 5 MiB limit.");
            }

            bytes = new byte[(int)length];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1)
            {
                throw new SpecInputException("Spec changed during reading.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            throw new SpecInputException("Spec file could not be read.");
        }

        string text;
        try
        {
            text = Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new SpecInputException("Spec must contain valid UTF-8.");
        }

        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        var inspection = new Inspection();
        try
        {
            if (extension == ".json")
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaximumDepth });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new SpecInputException("Spec root must be an object.");
                }
                inspection.ReadJson(document.RootElement, 1);
            }
            else
            {
                var parser = new Parser(new StringReader(text));
                parser.Consume<StreamStart>();
                parser.Consume<DocumentStart>();
                if (!parser.Accept<MappingStart>(out _))
                {
                    throw new SpecInputException("Spec root must be an object.");
                }
                inspection.ReadYaml(parser, 1);
                parser.Consume<DocumentEnd>();
                if (!parser.TryConsume<StreamEnd>(out _))
                {
                    throw new SpecInputException("Provide exactly one YAML document.");
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new SpecInputException("Invalid JSON syntax or nesting depth.");
        }
        catch (YamlException)
        {
            throw new SpecInputException("Invalid YAML syntax.");
        }

        var version = inspection.Version;
        if (version is null || !Regex.IsMatch(version, @"\A3\.[01]\.(0|[1-9][0-9]*)\z"))
        {
            throw new SpecInputException("OpenAPI version must be 3.0.x or 3.1.x.");
        }

        return new SpecDocument(extension, version[..3], bytes);
    }

    private sealed class Inspection
    {
        private int _references;
        internal string? Version { get; private set; }

        internal void ReadJson(JsonElement value, int depth, bool components = false)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in value.EnumerateObject())
                {
                    CheckProperty(keys, field.Name,
                        field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null,
                        depth, components);
                    ReadJson(field.Value, depth + 1, depth == 1 && field.Name == "components");
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    ReadJson(item, depth + 1);
                }
            }
        }

        internal void ReadYaml(IParser parser, int depth, bool components = false)
        {
            RejectUnsupportedYamlNode(parser.Current);
            if (parser.TryConsume<Scalar>(out _))
            {
                return;
            }

            if (depth > MaximumDepth)
            {
                throw new SpecInputException("YAML nesting depth exceeds 64.");
            }

            if (parser.TryConsume<SequenceStart>(out _))
            {
                while (!parser.TryConsume<SequenceEnd>(out _))
                {
                    ReadYaml(parser, depth + 1);
                }
                return;
            }

            parser.Consume<MappingStart>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                RejectUnsupportedYamlNode(parser.Current);
                var key = parser.Consume<Scalar>().Value;
                if (key == "<<")
                {
                    throw new SpecInputException("YAML merge keys are unsupported.");
                }

                CheckProperty(keys, key, (parser.Current as Scalar)?.Value, depth, components);
                ReadYaml(parser, depth + 1, depth == 1 && key == "components");
            }
        }

        private void CheckProperty(HashSet<string> keys, string key, string? value, int depth, bool components)
        {
            if (!keys.Add(key))
            {
                throw new SpecInputException("Duplicate key in OpenAPI spec.");
            }

            if (depth == 1 && key == "openapi")
            {
                Version = value;
            }

            if (key == "$ref")
            {
                if (value is null
                    || !Regex.IsMatch(value, @"\A#/(?:[A-Za-z0-9._~!$&'()*+,;=:@/?-]|%[0-9A-Fa-f]{2})*\z")
                    || Regex.IsMatch(Uri.UnescapeDataString(value), @"~(?![01])"))
                {
                    throw new SpecInputException("Only internal JSON Pointer references are supported.");
                }

                if (++_references > MaximumReferences)
                {
                    throw new SpecInputException("Spec exceeds the 2048 reference limit.");
                }
            }

            if (key is "$dynamicRef" or "$dynamicAnchor" or "$id" or "$anchor" or "$vocabulary")
            {
                throw new SpecInputException($"Unsupported OpenAPI schema keyword '{key}'.");
            }
            if ((key is "$schema" or "jsonSchemaDialect") && value != DefaultDialect)
            {
                throw new SpecInputException("Custom OpenAPI schema dialects are unsupported.");
            }
            if (components && key == "pathItems")
            {
                throw new SpecInputException("OpenAPI components.pathItems is unsupported.");
            }
        }

        private static void RejectUnsupportedYamlNode(ParsingEvent? node)
        {
            if (node is AnchorAlias)
            {
                throw new SpecInputException("YAML aliases are unsupported.");
            }
            if (node is NodeEvent { Tag.IsEmpty: false })
            {
                throw new SpecInputException("Explicit YAML tags are unsupported.");
            }
        }
    }
}
