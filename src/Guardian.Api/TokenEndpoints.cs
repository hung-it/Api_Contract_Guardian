using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace Guardian.Api;

internal static class TokenEndpoints
{
    internal static void Map(RouteGroupBuilder project)
    {
        project.MapGet("/tokens", List);
        project.MapPost("/tokens", Create);
        project.MapDelete("/tokens/{tokenId:guid}", Revoke);
        project.MapGet("/audit-events", History);
    }

    internal static void Audit(GuardianDb db, Guid projectId, Guid? actorId, string actorKind, string action,
        Guid entityId) => db.AuditEvents.Add(new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ActorId = actorId,
            ActorKind = actorKind,
            Action = action,
            EntityId = entityId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private static async Task<IResult> List(Guid projectId, ClaimsPrincipal user, GuardianDb db,
        CancellationToken cancellationToken)
    {
        if (await Maintainer(projectId, user, db, cancellationToken) is { } denied)
        {
            return denied;
        }
        var tokens = await db.Tokens.Where(token => token.ProjectId == projectId)
            .OrderByDescending(token => token.ExpiresAt).ThenBy(token => token.Id)
            .Select(token => new { token.Id, token.Scope, token.ExpiresAt, token.RevokedAt })
            .ToListAsync(cancellationToken);
        return Results.Ok(tokens);
    }

    private static async Task<IResult> Create(Guid projectId, TokenRequest input, HttpContext context,
        GuardianDb db, IAntiforgery antiforgery, CancellationToken cancellationToken)
    {
        if (await Maintainer(projectId, context.User, db, cancellationToken) is { } denied)
        {
            return denied;
        }
        var days = input.ExpiresInDays ?? 1;
        if (days is < 1 or > 30 || !await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var token = new IntegrationToken
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            TokenHash = Bootstrap.Hash(secret),
            Scope = Bootstrap.WriteScope,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(days),
        };
        db.Tokens.Add(token);
        Audit(db, projectId, ReviewerSession.UserId(context.User), "reviewer", "token.created", token.Id);
        await db.SaveChangesAsync(cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Created($"/api/v1/projects/{projectId}/tokens",
            new { token.Id, token.Scope, token.ExpiresAt, Token = secret });
    }

    private static async Task<IResult> Revoke(Guid projectId, Guid tokenId, HttpContext context,
        GuardianDb db, IAntiforgery antiforgery, CancellationToken cancellationToken)
    {
        if (await Maintainer(projectId, context.User, db, cancellationToken) is { } denied)
        {
            return denied;
        }
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var target = db.Tokens.Where(token => token.Id == tokenId && token.ProjectId == projectId);
        var changed = await target.Where(token => token.RevokedAt == null).ExecuteUpdateAsync(
            update => update.SetProperty(token => token.RevokedAt, DateTimeOffset.UtcNow), cancellationToken);
        if (changed == 1)
        {
            Audit(db, projectId, ReviewerSession.UserId(context.User), "reviewer", "token.revoked", tokenId);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!await target.AnyAsync(cancellationToken))
        {
            return Results.NotFound();
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> History(Guid projectId, ClaimsPrincipal user, GuardianDb db,
        CancellationToken cancellationToken)
    {
        if (await Maintainer(projectId, user, db, cancellationToken) is { } denied)
        {
            return denied;
        }
        return Results.Ok(await db.AuditEvents.AsNoTracking().Where(row => row.ProjectId == projectId)
            .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).Take(100)
            .ToListAsync(cancellationToken));
    }

    internal static async Task<IResult?> Maintainer(Guid projectId, ClaimsPrincipal user, GuardianDb db,
        CancellationToken cancellationToken)
    {
        var userId = ReviewerSession.UserId(user);
        var role = await db.Members.Where(member => member.ProjectId == projectId && member.UserId == userId)
            .Select(member => (ProjectRole?)member.Role).SingleOrDefaultAsync(cancellationToken);
        return role switch
        {
            null => Results.NotFound(),
            ProjectRole.Maintainer => null,
            _ => Results.StatusCode(403),
        };
    }

    internal sealed record TokenRequest(int? ExpiresInDays);
}
