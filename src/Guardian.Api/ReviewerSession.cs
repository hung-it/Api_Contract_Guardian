using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Guardian.Api;

internal static class ReviewerSession
{
    private static readonly PasswordHasher<Reviewer> Passwords = new();
    private static readonly Reviewer MissingUser = CreateMissingUser();

    internal static void Map(WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapGet("/csrf", (HttpContext context, IAntiforgery service) =>
            Results.Ok(new { token = service.GetAndStoreTokens(context).RequestToken }));
        auth.MapPost("/login", Login);
        auth.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }
            await context.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }

    internal static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<IResult> Login(LoginRequest input, HttpContext context, IAntiforgery antiforgery,
        GuardianDb db, CancellationToken cancellationToken)
    {
        if (!await antiforgery.IsRequestValidAsync(context)
            || input.Email is not { Length: > 0 and <= 254 } || input.Password is not { Length: > 0 and <= 256 })
        {
            return Results.BadRequest();
        }
        var email = input.Email.Trim().ToUpperInvariant();
        var reviewer = await db.Reviewers.SingleOrDefaultAsync(user => user.Email == email, cancellationToken);
        var candidate = reviewer ?? MissingUser;
        var verified = Passwords.VerifyHashedPassword(candidate, candidate.PasswordHash, input.Password);
        if (reviewer is null || verified == PasswordVerificationResult.Failed)
        {
            return Results.Unauthorized();
        }
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            reviewer.PasswordHash = Passwords.HashPassword(reviewer, input.Password);
            await db.SaveChangesAsync(cancellationToken);
        }
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, reviewer.Id.ToString()), new Claim(ClaimTypes.Name, reviewer.Email)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(new ClaimsPrincipal(identity));
        return Results.NoContent();
    }

    private static Reviewer CreateMissingUser()
    {
        var user = new Reviewer { Email = "", PasswordHash = "" };
        user.PasswordHash = Passwords.HashPassword(user, Guid.NewGuid().ToString());
        return user;
    }

    internal sealed record LoginRequest(string Email, string Password);
}
