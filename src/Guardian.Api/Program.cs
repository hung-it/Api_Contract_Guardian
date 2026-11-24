using System.Text.Json;
using Guardian.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = ReportIngestion.MaxReportBytes);
builder.Services.AddDbContext<GuardianDb>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Guardian")));
builder.Services.AddProblemDetails();
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
await app.RunAsync();

public partial class Program;
