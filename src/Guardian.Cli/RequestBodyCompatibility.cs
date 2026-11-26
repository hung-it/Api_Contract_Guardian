using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Guardian.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Guardian.Cli;

internal static class RequestBodyCompatibility
{
    private const string RuleId = "request-body-additional-properties-disallowed";
    private static readonly Regex PathParameter = new(@"\{[^{}]+\}");
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    internal static IReadOnlyList<ContractFinding> Compare(SpecDocument baseline, SpecDocument candidate)
    {
        if (baseline.Bytes.AsSpan().SequenceEqual(candidate.Bytes))
        {
            return [];
        }

        try
        {
            return new Comparison(Read(baseline), Read(candidate)).FindChanges();
        }
        catch (Exception error) when (error is JsonException or YamlException or ArgumentException or InvalidOperationException)
        {
            throw new EngineException("Could not inspect the validated request body schemas.");
        }
    }

    private sealed class Comparison(JsonObject baseline, JsonObject candidate)
    {
        private readonly List<ContractFinding> _findings = [];
        private readonly HashSet<(JsonObject, JsonObject)> _ancestors = [];
        private int _visited;

        internal IReadOnlyList<ContractFinding> FindChanges()
        {
            if (baseline["paths"] is not JsonObject before || candidate["paths"] is not JsonObject after)
            {
                return [];
            }

            var routes = after.Where(entry => entry.Key.StartsWith('/'))
                .ToLookup(entry => PathParameter.Replace(entry.Key, "{}"), StringComparer.Ordinal);
            foreach (var (path, baselineItem) in before.Where(entry => entry.Key.StartsWith('/')))
            {
                var candidateItem = after[path];
                if (candidateItem is null)
                {
                    var matches = routes[PathParameter.Replace(path, "{}")].Take(2).ToArray();
                    candidateItem = matches.Length == 1 ? matches[0].Value : null;
                }

                var oldPath = Resolve(baseline, baselineItem);
                var newPath = Resolve(candidate, candidateItem);
                foreach (var method in Methods)
                {
                    var oldBody = (oldPath?[method] as JsonObject)?["requestBody"];
                    var newBody = (newPath?[method] as JsonObject)?["requestBody"];
                    if (Resolve(baseline, oldBody)?["content"] is not JsonObject oldContent
                        || Resolve(candidate, newBody)?["content"] is not JsonObject newContent)
                    {
                        continue;
                    }
                    foreach (var (mediaType, oldMedia) in oldContent)
                    {
                        CompareSchema((oldMedia as JsonObject)?["schema"], (newContent[mediaType] as JsonObject)?["schema"],
                            method.ToUpperInvariant(), path, mediaType, "#", 0);
                    }
                }
            }
            return _findings;
        }

        private void CompareSchema(JsonNode? before, JsonNode? after, string method, string path,
            string mediaType, string location, int depth)
        {
            if (IsBoolean((before as JsonObject)?["readOnly"], true) || IsBoolean((after as JsonObject)?["readOnly"], true)
                || Resolve(baseline, before, schema: true) is not { } oldSchema
                || Resolve(candidate, after, schema: true) is not { } newSchema
                || HasComplexConstraints(oldSchema) || HasComplexConstraints(newSchema)
                || !_ancestors.Add((oldSchema, newSchema)))
            {
                return;
            }

            if (depth > 64 || ++_visited > 65_536)
            {
                throw new EngineException("Request body compatibility analysis exceeded its schema traversal limit.");
            }

            var oldAdditional = oldSchema["additionalProperties"];
            if (AllowsType(oldSchema, "object") && AllowsType(newSchema, "object"))
            {
                if ((oldAdditional is null || IsBoolean(oldAdditional, true))
                    && IsBoolean(newSchema["additionalProperties"], false))
                {
                    var fingerprint = FindingNormalizer.Fingerprint(
                        ["guardian-finding-v1", "guardian", RuleId, method, path, mediaType, location]);
                    _findings.Add(new(RuleId, FindingSeverity.ERR, method, path, "paths", null, null,
                        $"The {mediaType} request body schema at {location} no longer allows additional properties.", fingerprint));
                }
                if (oldSchema["properties"] is JsonObject oldProperties && newSchema["properties"] is JsonObject newProperties)
                {
                    foreach (var (name, property) in oldProperties)
                    {
                        var token = name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                        CompareSchema(property, newProperties[name], method, path, mediaType, location + "/properties/" + token, depth + 1);
                    }
                }
            }
            if (AllowsType(oldSchema, "array") && AllowsType(newSchema, "array"))
            {
                CompareSchema(oldSchema["items"], newSchema["items"], method, path, mediaType, location + "/items", depth + 1);
            }
            _ancestors.Remove((oldSchema, newSchema));
        }
    }

    private static bool HasComplexConstraints(JsonObject schema) => IsBoolean(schema["readOnly"], true)
        || schema.Any(field => field.Key is "allOf" or "oneOf" or "anyOf" or "not" or "if" or "then" or "else"
            or "const" or "enum" or "patternProperties" or "propertyNames" or "unevaluatedProperties" or "dependentSchemas");

    private static bool AllowsType(JsonObject schema, string type) => schema["type"] switch
    {
        null => true,
        JsonArray types => types.Any(value => IsString(value, type)),
        var value => IsString(value, type),
    };

    private static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue scalar && scalar.TryGetValue<string>(out var actual) && actual == expected;

    private static bool IsBoolean(JsonNode? node, bool expected) =>
        node is JsonValue scalar && scalar.TryGetValue<bool>(out var actual) && actual == expected;

    private static JsonObject? Resolve(JsonObject document, JsonNode? node, bool schema = false)
    {
        HashSet<JsonObject>? chain = null;
        while (node is JsonObject current && current["$ref"] is JsonValue value
            && value.TryGetValue<string>(out var reference))
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal) || !(chain ??= []).Add(current)
                || (schema && current.Any(field => field.Key is not ("$ref" or "title" or "description" or "example" or "examples"))))
            {
                return null;
            }

            node = document;
            foreach (var segment in Uri.UnescapeDataString(reference[2..]).Split('/'))
            {
                var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                node = node switch
                {
                    JsonObject mapping => mapping[key],
                    JsonArray sequence when (key.Length == 1 || !key.StartsWith('0'))
                        && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        && index < sequence.Count => sequence[index],
                    _ => null,
                };
            }
        }
        return node as JsonObject;
    }

    private static JsonObject Read(SpecDocument spec)
    {
        var text = Encoding.UTF8.GetString(spec.Bytes).TrimStart('\uFEFF');
        if (spec.Extension == ".json")
        {
            return JsonNode.Parse(text)!.AsObject();
        }

        var yaml = new YamlStream();
        using var reader = new StringReader(text);
        yaml.Load(reader);
        return ReadYaml(yaml.Documents[0].RootNode)!.AsObject();
    }

    private static JsonNode? ReadYaml(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(entry =>
            KeyValuePair.Create(((YamlScalarNode)entry.Key).Value ?? "", ReadYaml(entry.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ReadYaml).ToArray()),
        YamlScalarNode { Style: ScalarStyle.Plain } scalar when bool.TryParse(scalar.Value, out var boolean) => JsonValue.Create(boolean),
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => throw new EngineException("Could not read the validated YAML schema."),
    };
}
