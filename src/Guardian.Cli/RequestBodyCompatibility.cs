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
    private const string ClosedObject = "request-body-additional-properties-disallowed";
    private const string NarrowedProperty = "request-body-optional-property-narrowed";
    private const string Unproven = "request-body-compatibility-unproven";
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
            if (before is null || after is null
                || IsBoolean((before as JsonObject)?["readOnly"], true)
                || IsBoolean((after as JsonObject)?["readOnly"], true))
            {
                return;
            }

            CheckBudget(depth);
            var oldSchema = Resolve(baseline, before, schema: true);
            var newSchema = Resolve(candidate, after, schema: true);
            if (oldSchema is null || newSchema is null
                || HasComplexConstraints(oldSchema) || HasComplexConstraints(newSchema))
            {
                if (!Equivalent(before, after))
                {
                    Finding(Unproven, FindingSeverity.UNKNOWN,
                        "Compatibility of this changed request schema could not be proven.",
                        method, path, mediaType, location);
                }
                return;
            }

            if (!_ancestors.Add((oldSchema, newSchema)))
            {
                return;
            }

            if (AllowsType(oldSchema, "object") && AllowsType(newSchema, "object"))
            {
                var oldAdditional = oldSchema["additionalProperties"];
                var oldProperties = oldSchema["properties"] as JsonObject;
                var newProperties = newSchema["properties"] as JsonObject;
                if (!IsBoolean(oldAdditional, false) && HasRoomForExtraProperty(oldSchema, oldProperties))
                {
                    if (IsBoolean(newSchema["additionalProperties"], false))
                    {
                        var definite = IsOpen(oldAdditional) || IsSimpleTypedSchema(Resolve(baseline, oldAdditional));
                        Finding(definite ? ClosedObject : Unproven,
                            definite ? FindingSeverity.ERR : FindingSeverity.UNKNOWN,
                            definite ? "Additional properties are no longer allowed."
                                : "The previous additional-property schema could not be proven empty.",
                            method, path, mediaType, location);
                    }

                    if (newProperties is not null)
                    {
                        foreach (var (name, property) in newProperties)
                        {
                            if (oldProperties?.ContainsKey(name) == true || IsRequired(newSchema, name)
                                || IsBoolean((property as JsonObject)?["readOnly"], true)
                                || Equivalent(oldAdditional ?? JsonValue.Create(true), property))
                            {
                                continue;
                            }
                            var definition = Resolve(candidate, property, schema: true);
                            if (IsOpen(oldAdditional) && (IsBoolean(property, true)
                                || definition?.All(field => IsAnnotation(field.Key)) == true))
                            {
                                continue;
                            }
                            var definite = IsOpen(oldAdditional) && (IsBoolean(property, false)
                                || IsSimpleTypedSchema(definition));
                            Finding(definite ? NarrowedProperty : Unproven,
                                definite ? FindingSeverity.ERR : FindingSeverity.UNKNOWN,
                                definite ? "The new optional property restricts previously allowed values."
                                    : "Compatibility of the new optional property could not be proven.",
                                method, path, mediaType, PropertyLocation(location, name));
                        }
                    }
                }
                if (oldProperties is not null && newProperties is not null)
                {
                    foreach (var (name, property) in oldProperties)
                    {
                        CompareSchema(property, newProperties[name], method, path, mediaType,
                            PropertyLocation(location, name), depth + 1);
                    }
                }
            }
            if (AllowsType(oldSchema, "array") && AllowsType(newSchema, "array"))
            {
                CompareSchema(oldSchema["items"], newSchema["items"], method, path, mediaType, location + "/items", depth + 1);
            }
            _ancestors.Remove((oldSchema, newSchema));
        }

        private void CheckBudget(int depth)
        {
            if (depth > 64 || ++_visited > 65_536)
            {
                throw new EngineException("Request body compatibility analysis exceeded its schema traversal limit.");
            }
        }

        private void Finding(string rule, FindingSeverity severity, string message, string method, string path,
            string mediaType, string location)
        {
            var fingerprint = FindingNormalizer.Fingerprint(
                ["guardian-finding-v1", "guardian", rule, method, path, mediaType, location]);
            _findings.Add(new(rule, severity, method, path, "paths", null, null,
                $"{message} Request body {mediaType} at {location}.", fingerprint));
        }

        private bool HasRoomForExtraProperty(JsonObject schema, JsonObject? properties)
        {
            if (schema["maxProperties"] is not JsonValue cap || !cap.TryGetValue<int>(out var maximum))
            {
                return true;
            }
            var occupied = (schema["required"] as JsonArray)?.Count(name => name is JsonValue key
                && key.TryGetValue<string>(out var value)
                && !IsBoolean(Resolve(baseline, properties?[value])?["readOnly"], true)) ?? 0;
            return occupied < maximum;
        }

        private bool Equivalent(JsonNode? before, JsonNode? after) =>
            JsonNode.DeepEquals(ValidationShape(baseline, before, [], 0), ValidationShape(candidate, after, [], 0));

        private JsonNode? ValidationShape(JsonObject document, JsonNode? node, HashSet<JsonObject> ancestors, int depth)
        {
            CheckBudget(depth);
            if (node is not JsonObject schema)
            {
                return node?.DeepClone();
            }
            if (!ancestors.Add(schema))
            {
                return JsonValue.Create("$recursive");
            }

            var shape = new JsonObject();
            foreach (var (key, value) in schema)
            {
                if (IsAnnotation(key))
                {
                    continue;
                }
                shape[key] = (key, value) switch
                {
                    ("$ref", _) when Resolve(document, schema) is { } target =>
                        ValidationShape(document, target, ancestors, depth + 1),
                    ("properties" or "patternProperties" or "dependentSchemas", JsonObject map) =>
                        new JsonObject(map.Select(item => KeyValuePair.Create(item.Key,
                            ValidationShape(document, item.Value, ancestors, depth + 1)))),
                    ("allOf" or "anyOf" or "oneOf" or "prefixItems", JsonArray list) =>
                        new JsonArray(list.Select(item => ValidationShape(document, item, ancestors, depth + 1)).ToArray()),
                    ("items" or "additionalProperties" or "not" or "if" or "then" or "else" or "contains"
                       or "propertyNames" or "unevaluatedProperties" or "unevaluatedItems" or "additionalItems", _) =>
                        ValidationShape(document, value, ancestors, depth + 1),
                    _ => value?.DeepClone(),
                };
            }
            ancestors.Remove(schema);
            return shape;
        }
    }

    private static bool IsOpen(JsonNode? node) => node is null || IsBoolean(node, true);

    private static bool IsAnnotation(string key) =>
        key is "title" or "description" or "example" or "examples" or "externalDocs" or "$comment";

    private static bool IsSimpleTypedSchema(JsonObject? schema) => schema is not null
        && schema["type"] is JsonValue value && value.TryGetValue<string>(out var type)
        && type is "string" or "number" or "integer" or "boolean" or "object" or "array" or "null"
        && schema.All(field => field.Key is "type" or "nullable" || IsAnnotation(field.Key));

    private static bool IsRequired(JsonObject schema, string name) =>
        schema["required"] is JsonArray list && list.Any(item => IsString(item, name));

    private static string PropertyLocation(string location, string name) => location + "/properties/"
        + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

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
        YamlScalarNode { Style: ScalarStyle.Plain } scalar
            when int.TryParse(scalar.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            => JsonValue.Create(integer),
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => throw new EngineException("Could not read the validated YAML schema."),
    };
}
