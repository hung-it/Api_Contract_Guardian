using System.Text.Json;
using Guardian.Api;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = ReportIngestion.MaxReportBytes);
builder.Services.AddDbContext<GuardianDb>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Guardian")));
builder.Services.AddProblemDetails();
var keyDirectory = builder.Configuration["Guardian:DataProtectionKeys"];
if (keyDirectory is not null)
{
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
        .SetApplicationName("Guardian");
}
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "guardian.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("Guardian:AllowHttpLoopback")
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(2);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = 403;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("Guardian:AllowHttpLoopback")
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();
app.UseExceptionHandler();
if (args is ["migrate"] or ["bootstrap"])
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<GuardianDb>();
    if (args[0] == "migrate")
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await Bootstrap.Run(db, app.Configuration);
    }
    return;
}

app.MapPost("/api/v1/projects/{projectId:guid}/ci-reports", ReportIngestion.Publish);
app.UseAuthentication();
app.UseAuthorization();
ReviewerSession.Map(app);
HistoryEndpoints.Map(app.MapGroup("/api/v1").RequireAuthorization());
TokenEndpoints.Map(app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization());
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (GuardianDb db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(503));
await app.RunAsync();

public partial class Program;
