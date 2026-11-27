using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Guardian.Api;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = ReportIngestion.MaxReportBytes);
builder.Services.AddDbContext<GuardianDb>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Guardian")));
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient<CiVerification>(client => client.DefaultRequestHeaders.UserAgent.ParseAdd("Guardian"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
var proxySetting = builder.Configuration["Guardian:TrustedProxy"];
if (!string.IsNullOrEmpty(proxySetting))
{
    if (!IPAddress.TryParse(proxySetting, out var proxy))
    {
        throw new InvalidOperationException("Guardian:TrustedProxy must be an IP address.");
    }
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Add(proxy);
        options.ForwardLimit = 1;
    });
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("reviewer-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay))
        {
            context.HttpContext.Response.Headers.RetryAfter = FormattableString.Invariant(
                $"{Math.Max(1, Math.Ceiling(delay.TotalSeconds))}");
        }
        return ValueTask.CompletedTask;
    };
});
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
if (!string.IsNullOrEmpty(proxySetting))
{
    app.UseForwardedHeaders();
}
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
app.UseRateLimiter();
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
