using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Guardian.Api;

internal static class HistoryEndpoints
{
    internal static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/projects", Projects);
        api.MapGet("/projects/{projectId:guid}/analyses", History);
        api.MapGet("/analyses/{analysisId:guid}", Detail);
        api.MapGet("/analyses/{analysisId:guid}/findings", Findings);
    }

    private static async Task<IResult> Projects(ClaimsPrincipal user, GuardianDb db, CancellationToken cancellationToken)
    {
        var userId = ReviewerSession.UserId(user);
        var projects = from member in db.Members
                       join project in db.Projects on member.ProjectId equals project.Id
                       where member.UserId == userId
                       orderby project.Name, project.Id
                       select new { project.Id, project.Name, member.Role };
        var rows = await projects.ToListAsync(cancellationToken);
        return Results.Ok(rows.Select(project => new { project.Id, project.Name, Role = project.Role.ToString() }));
    }

    private static async Task<IResult> History(Guid projectId, int? limit, string? cursor, ClaimsPrincipal user,
        GuardianDb db, CancellationToken cancellationToken)
    {
        var take = limit ?? 25;
        if (take is < 1 or > 100 || !TryReadCursor(cursor, out var time, out var id))
        {
            return Results.BadRequest();
        }
        var projects = MemberProjects(db, user);
        if (!await projects.ContainsAsync(projectId, cancellationToken))
        {
            return Results.NotFound();
        }
        var query = db.Analyses.Where(analysis => analysis.ProjectId == projectId && projects.Contains(analysis.ProjectId));
        if (cursor is not null)
        {
            query = query.Where(row => EF.Functions.LessThan(
                ValueTuple.Create(row.CreatedAt, row.Id), ValueTuple.Create(time, id)));
        }
        var page = await query.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .Select(row => new { row.Id, row.Status, row.CreatedAt }).Take(take + 1).ToListAsync(cancellationToken);
        var next = page.Count > take
            ? WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes(
                FormattableString.Invariant($"{page[take - 1].CreatedAt.UtcTicks}:{page[take - 1].Id}"))) : null;
        return Results.Ok(new { items = page.Take(take), next_cursor = next });
    }

    private static async Task<IResult> Detail(Guid analysisId, ClaimsPrincipal user, GuardianDb db,
        CancellationToken cancellationToken)
    {
        var row = await Visible(db, user).SingleOrDefaultAsync(analysis => analysis.Id == analysisId, cancellationToken);
        if (row is null)
        {
            return Results.NotFound();
        }
        var report = JsonSerializer.Deserialize<JsonElement>(row.Report);
        return Results.Ok(new
        {
            row.Id,
            row.ProjectId,
            row.Status,
            row.CreatedAt,
            Summary = report.GetProperty("summary"),
            Provenance = report.GetProperty("provenance"),
            Diagnostics = report.GetProperty("diagnostics"),
        });
    }

    private static async Task<IResult> Findings(Guid analysisId, int? limit, int? after, string? severity, string? rule,
        ClaimsPrincipal user, GuardianDb db, CancellationToken cancellationToken)
    {
        var take = limit ?? 25;
        if (take is < 1 or > 100 || after is < 0 || severity is not (null or "ERR" or "WARN" or "INFO" or "UNKNOWN"))
        {
            return Results.BadRequest();
        }
        var visible = Visible(db, user).Where(analysis => analysis.Id == analysisId);
        if (!await visible.AnyAsync(cancellationToken))
        {
            return Results.NotFound();
        }
        var query = db.Findings.Where(finding => visible.Select(analysis => analysis.Id).Contains(finding.AnalysisId)
            && finding.Ordinal > (after ?? -1));
        if (severity is not null)
        {
            query = query.Where(finding => finding.Severity == severity);
        }
        if (rule is not null)
        {
            query = query.Where(finding => finding.RuleId == rule);
        }
        var page = await query.OrderBy(finding => finding.Ordinal)
            .Select(finding => new { finding.Ordinal, finding.Detail }).Take(take + 1).ToListAsync(cancellationToken);
        return Results.Ok(new
        {
            items = page.Take(take).Select(row => JsonSerializer.Deserialize<JsonElement>(row.Detail)),
            next_after = page.Count > take ? (int?)page[take - 1].Ordinal : null,
        });
    }

    private static IQueryable<Analysis> Visible(GuardianDb db, ClaimsPrincipal user) =>
        db.Analyses.AsNoTracking().Where(row => MemberProjects(db, user).Contains(row.ProjectId));

    private static IQueryable<Guid> MemberProjects(GuardianDb db, ClaimsPrincipal user)
    {
        var userId = ReviewerSession.UserId(user);
        return db.Members.Where(member => member.UserId == userId).Select(member => member.ProjectId);
    }

    private static bool TryReadCursor(string? cursor, out DateTimeOffset time, out Guid id)
    {
        time = default;
        id = default;
        if (cursor is null)
        {
            return true;
        }
        if (cursor.Length > 96)
        {
            return false;
        }
        try
        {
            var parts = Encoding.ASCII.GetString(WebEncoders.Base64UrlDecode(cursor)).Split(':');
            if (parts is not [var ticksText, var idText]
                || !long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks
                || !Guid.TryParseExact(idText, "D", out id))
            {
                return false;
            }
            time = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
