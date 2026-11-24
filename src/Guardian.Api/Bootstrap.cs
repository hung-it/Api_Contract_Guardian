using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Guardian.Api;

internal static class Bootstrap
{
    internal const string WriteScope = "reports:write";

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static bool IsToken(string token) => token.Length == 64 && token.All(Uri.IsHexDigit);

    internal static async Task Run(GuardianDb db, IConfiguration settings)
    {
        var projectId = settings.GetValue<Guid>("Guardian:ProjectId");
        if (projectId == Guid.Empty)
        {
            throw new InvalidOperationException("Set Guardian__ProjectId for bootstrap.");
        }
        var raw = settings["Guardian:BootstrapToken"];
        if (raw is not null && !IsToken(raw))
        {
            throw new InvalidOperationException("Provide a randomly generated 32-byte token as 64 hex characters.");
        }

        if (!await db.Projects.AnyAsync(project => project.Id == projectId))
        {
            db.Projects.Add(new() { Id = projectId, Name = settings["Guardian:ProjectName"] ?? "FlowForge" });
        }
        if (raw is not null)
        {
            var hash = Hash(raw);
            var existing = await db.Tokens.SingleOrDefaultAsync(token => token.TokenHash == hash);
            if (existing is null)
            {
                var token = new IntegrationToken
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    TokenHash = hash,
                    Scope = WriteScope,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                };
                db.Tokens.Add(token);
                TokenEndpoints.Audit(db, projectId, null, "bootstrap", "token.created", token.Id);
            }
            else if (existing.ProjectId != projectId)
            {
                throw new InvalidOperationException("The bootstrap token belongs to another project.");
            }
        }
        var email = settings["Guardian:ReviewerEmail"]?.Trim().ToUpperInvariant();
        var password = settings["Guardian:ReviewerPassword"];
        if (email is not null || password is not null)
        {
            if (email is not { Length: > 0 and <= 254 } || password is not { Length: >= 12 and <= 256 })
            {
                throw new InvalidOperationException("Provide a reviewer email and a password of 12 to 256 characters.");
            }
            var reviewer = await db.Reviewers.SingleOrDefaultAsync(user => user.Email == email);
            if (reviewer is null)
            {
                reviewer = new() { Id = Guid.NewGuid(), Email = email, PasswordHash = "" };
                reviewer.PasswordHash = new PasswordHasher<Reviewer>().HashPassword(reviewer, password);
                db.Reviewers.Add(reviewer);
            }
            if (!await db.Members.AnyAsync(member => member.ProjectId == projectId && member.UserId == reviewer.Id))
            {
                db.Members.Add(new() { ProjectId = projectId, UserId = reviewer.Id, Role = ProjectRole.Maintainer });
            }
        }
        await db.SaveChangesAsync();
    }
}
