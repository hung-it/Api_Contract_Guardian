using System.Text;
using Guardian.Cli;
using Xunit;

namespace Guardian.Core.Tests;

public sealed class SpecLoaderTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("guardian spec tests ");

    [Theory]
    [InlineData(".yaml", "openapi: 3.0.4\npaths: {}")]
    [InlineData(".yml", "openapi: '3.0.0'\nx-ref: {$ref: '#/components/schemas/Example'}")]
    [InlineData(".json", """{"openapi":"3.1.2","paths":{}}""")]
    [InlineData(".yaml", "openapi: 3.1.0\njsonSchemaDialect: https://spec.openapis.org/oas/3.1/dialect/base")]
    [InlineData(".yaml", "openapi: 3.1.0\ncomponents: {schemas: {X: {type: string}}}")]
    public void LoadsSupportedInput(string extension, string contents)
    {
        var path = Write(extension, contents);

        var document = SpecLoader.Load(path);

        Assert.Equal(extension, document.Extension);
        Assert.StartsWith("3.", document.Dialect, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(contents), document.Bytes);
    }

    [Theory]
    [InlineData(".json", """{"openapi":"3.1.0","openapi":"3.0.3"}""", "duplicate")]
    [InlineData(".json", """{"openapi":"3.1.0","paths":,}""", "syntax")]
    [InlineData(".json", """{"openapi":"3.1.0","info":{"title":"\uD800"}}""", "syntax")]
    [InlineData(".json", """["3.1.0"]""", "object")]
    [InlineData(".json", """{"openapi":3.1}""", "version")]
    [InlineData(".json", """{"paths":{}}""", "version")]
    [InlineData(".yaml", "openapi: 3.2.0", "version")]
    [InlineData(".yaml", "openapi: 3.1.0\nopenapi: 3.0.3", "duplicate")]
    [InlineData(".yaml", "openapi: 3.1.0\n---\nopenapi: 3.1.0", "one YAML document")]
    [InlineData(".yaml", "openapi: 3.1.0\nx-value: &name value\nx-alias: *name", "alias")]
    [InlineData(".yaml", "openapi: 3.1.0\nx-value: {<<: {name: value}}", "merge")]
    [InlineData(".yaml", "openapi: 3.1.0\nx-value: !example value", "tag")]
    [InlineData(".yaml", "openapi: 3.1.0\nx-value: {$ref: ./other.yaml}", "internal JSON Pointer")]
    [InlineData(".json", """{"openapi":"3.1.0","x-ref":{"$ref":"https://example.com/model.json"}}""", "internal JSON Pointer")]
    [InlineData(".json", """{"openapi":"3.1.0","x-ref":{"$ref":"#/invalid~2"}}""", "internal JSON Pointer")]
    [InlineData(".yaml", "openapi: 3.1.0\nx-ref: {$dynamicRef: '#name'}", "unsupported")]
    [InlineData(".yaml", "openapi: 3.1.0\nx-schema: {$id: 'https://example.com/schema'}", "unsupported")]
    [InlineData(".yaml", "openapi: 3.1.0\ncomponents: {pathItems: {Item: {}}}", "pathItems")]
    [InlineData(".json", """{"openapi":"3.1.0","jsonSchemaDialect":"https://example.com/dialect"}""", "dialect")]
    public void RejectsUnsupportedInput(string extension, string contents, string expected)
    {
        var error = Assert.Throws<SpecInputException>(() => SpecLoader.Load(Write(extension, contents)));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("https://example.com/openapi.yaml")]
    [InlineData("main:openapi.yaml")]
    [InlineData("missing.yaml")]
    public void RejectsPathsOutsideTheFileInputContract(string path)
    {
        Assert.Throws<SpecInputException>(() => SpecLoader.Load(path));
    }

    [Fact]
    public void RejectsInvalidUtf8()
    {
        var path = Path.Combine(_directory.FullName, "invalid.json");
        File.WriteAllBytes(path, [0xff]);

        var error = Assert.Throws<SpecInputException>(() => SpecLoader.Load(path));

        Assert.Contains("UTF-8", error.Message);
    }

    [Fact]
    public void RejectsFileOverFiveMebibytes()
    {
        var path = Path.Combine(_directory.FullName, "oversized.yaml");
        using (var file = File.Create(path))
        {
            file.SetLength(5 * 1024 * 1024 + 1);
        }

        var error = Assert.Throws<SpecInputException>(() => SpecLoader.Load(path));

        Assert.Contains("5 MiB", error.Message);
    }

    [Theory]
    [InlineData(".yaml", "openapi: 3.1.0\nx-nested: ")]
    [InlineData(".json", """{"openapi":"3.1.0","x-nested":""")]
    public void RejectsDepthOver64(string extension, string prefix)
    {
        var contents = prefix + new string('[', 64) + "0" + new string(']', 64);
        if (extension == ".json")
        {
            contents += "}";
        }

        var error = Assert.Throws<SpecInputException>(() => SpecLoader.Load(Write(extension, contents)));

        Assert.Contains("depth", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsMoreThan2048References()
    {
        var contents = "openapi: 3.1.0\nx-refs:\n"
            + string.Concat(Enumerable.Repeat("  - {$ref: '#/components/schemas/Example'}\n", 2049));

        var error = Assert.Throws<SpecInputException>(() => SpecLoader.Load(Write(".yml", contents)));

        Assert.Contains("2048", error.Message);
    }

    private string Write(string extension, string contents)
    {
        var path = Path.Combine(_directory.FullName, "input" + extension);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _directory.EnumerateFiles())
        {
            file.Delete();
        }
        _directory.Delete();
    }
}
