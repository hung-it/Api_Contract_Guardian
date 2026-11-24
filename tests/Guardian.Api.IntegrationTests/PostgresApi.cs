using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Guardian.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Guardian.Api.IntegrationTests;

public abstract class PostgresApi : IAsyncLifetime
{
    private readonly string _connection = new NpgsqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("GUARDIAN_TEST_POSTGRES")
        ?? throw new InvalidOperationException("Set GUARDIAN_TEST_POSTGRES to a disposable PostgreSQL server."))
    {
        Database = $"guardian_test_{Guid.NewGuid():N}",
        IncludeErrorDetail = false,
    }.ConnectionString;

    private WebApplicationFactory<Program> _factory = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected Guid ProjectId { get; } = Guid.NewGuid();
    protected Guid OtherProjectId { get; } = Guid.NewGuid();
    protected string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    protected GuardianDb Database() => new(new DbContextOptionsBuilder<GuardianDb>().UseNpgsql(_connection).Options);

    public async Task InitializeAsync()
    {
        await using var db = Database();
        await db.Database.MigrateAsync();
        db.Projects.AddRange(new Project { Id = ProjectId, Name = "FlowForge" },
            new Project { Id = OtherProjectId, Name = "Separate project" });
        db.Tokens.Add(CreateToken(Token, ProjectId, "reports:write", DateTimeOffset.UtcNow.AddDays(1)));
        await db.SaveChangesAsync();
        _factory = new ApiFactory(_connection);
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    protected static IntegrationToken CreateToken(string raw, Guid project, string scope, DateTimeOffset expiresAt) =>
        new()
        {
            Id = Guid.NewGuid(), ProjectId = project, Scope = scope, ExpiresAt = expiresAt,
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
        };

    protected static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json"));

    protected async Task<HttpResponseMessage> Publish(string report, string? token, Guid? project = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{project ?? ProjectId}/ci-reports")
        {
            Content = new StringContent(report, Encoding.UTF8, "application/json"),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            Client.Dispose();
            await _factory.DisposeAsync();
        }
        await using var db = Database();
        await db.Database.EnsureDeletedAsync();
    }

    private sealed class ApiFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, values) => values.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Guardian"] = connection }));
    }
}
