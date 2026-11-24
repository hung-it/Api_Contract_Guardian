using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Guardian.Api;

public sealed class GuardianDb(DbContextOptions<GuardianDb> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<IntegrationToken> Tokens => Set<IntegrationToken>();
    public DbSet<Analysis> Analyses => Set<Analysis>();
    public DbSet<Finding> Findings => Set<Finding>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Project>().ToTable("projects");
        model.Entity<IntegrationToken>().ToTable("integration_tokens").HasOne<Project>().WithMany()
            .HasForeignKey(token => token.ProjectId);
        model.Entity<IntegrationToken>().HasIndex(token => token.TokenHash).IsUnique();
        model.Entity<Analysis>().ToTable("analyses").HasOne<Project>().WithMany()
            .HasForeignKey(analysis => analysis.ProjectId);
        model.Entity<Analysis>().HasIndex(analysis => new { analysis.ProjectId, analysis.AnalysisKey })
            .IsUnique().HasDatabaseName("ux_analyses_project_key");
        model.Entity<Analysis>().Property(analysis => analysis.Report).HasColumnType("jsonb");
        model.Entity<Finding>().ToTable("findings").HasOne<Analysis>().WithMany(analysis => analysis.Findings)
            .HasForeignKey(finding => finding.AnalysisId);
        model.Entity<Finding>().HasKey(finding => new { finding.AnalysisId, finding.Ordinal });
        model.Entity<Finding>().Property(finding => finding.Detail).HasColumnType("jsonb");

        foreach (var type in model.Model.GetEntityTypes())
        {
            foreach (var property in type.GetProperties())
            {
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
            }
        }
    }
}

public sealed class Project
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
}

public sealed class IntegrationToken
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string TokenHash { get; set; }
    public required string Scope { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class Analysis
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string AnalysisKey { get; set; }
    public required string Status { get; set; }
    public required string Report { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Finding> Findings { get; } = [];
}

public sealed class Finding
{
    public Guid AnalysisId { get; set; }
    public int Ordinal { get; set; }
    public required string RuleId { get; set; }
    public required string Severity { get; set; }
    public required string Detail { get; set; }
}
