using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Guardian.Cli;

internal static class ReportPublisher
{
    private const int MaxReportBytes = 10 * 1024 * 1024;
    private const int PublishFailure = 6;

    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (!TryParse(arguments, out var reportPath, out var endpoint))
        {
            Console.Error.WriteLine("Invalid arguments. Run 'guardian --help' for usage.");
            return 1;
        }
        var token = Environment.GetEnvironmentVariable("GUARDIAN_REPORT_TOKEN");
        if (token is not { Length: 64 } || !token.All(Uri.IsHexDigit))
        {
            Console.Error.WriteLine("Set GUARDIAN_REPORT_TOKEN to a Guardian integration token.");
            return PublishFailure;
        }

        byte[] report;
        try
        {
            await using var file = File.OpenRead(reportPath);
            if (file.Length > MaxReportBytes)
            {
                Console.Error.WriteLine("Report exceeds 10 MiB.");
                return PublishFailure;
            }
            report = new byte[(int)file.Length];
            await file.ReadExactlyAsync(report);
            if (file.ReadByte() != -1)
            {
                Console.Error.WriteLine("Report changed during reading.");
                return PublishFailure;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            Console.Error.WriteLine("Could not read the report file.");
            return PublishFailure;
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = !endpoint.IsLoopback,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 64 * 1024,
        };
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = await SendAsync(client, endpoint, token, report, cancellation.Token);
            Console.WriteLine(result);
            return 0;
        }
        catch (PublishException error)
        {
            Console.Error.WriteLine(error.Message);
            return PublishFailure;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Report publish canceled.");
            return PublishFailure;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    internal static async Task<string> SendAsync(HttpClient client, Uri endpoint, string token, byte[] report,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var delay = TimeSpan.FromMilliseconds(250 * attempt);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new ByteArrayContent(report);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var json = JsonDocument.Parse(responseBody);
                    if (json.RootElement.ValueKind == JsonValueKind.Object
                        && json.RootElement.TryGetProperty("analysis_id", out var id)
                        && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out _))
                    {
                        return JsonSerializer.Serialize(json.RootElement);
                    }
                    throw new PublishException("API returned an invalid publish response.");
                }
                if (attempt == 3 || response.StatusCode is not (HttpStatusCode.TooManyRequests
                    or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                {
                    throw new PublishException($"API rejected report publish (HTTP {(int)response.StatusCode}).");
                }
                var retryAfter = response.Headers.RetryAfter;
                var requestedDelay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
                if (requestedDelay > TimeSpan.FromSeconds(2))
                {
                    throw new PublishException($"API requested a later retry (HTTP {(int)response.StatusCode}).");
                }
                if (requestedDelay > delay)
                {
                    delay = requestedDelay.Value;
                }
            }
            catch (JsonException)
            {
                throw new PublishException("API returned an invalid publish response.");
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                && error is HttpRequestException or OperationCanceledException or IOException)
            {
                if (attempt == 3)
                {
                    throw new PublishException("Could not reach the Guardian API after three attempts.");
                }
            }
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool TryParse(string[] arguments, out string report, out Uri endpoint)
    {
        report = null!;
        endpoint = null!;
        if (arguments.Length != 6)
        {
            return false;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (string.IsNullOrWhiteSpace(arguments[index + 1])
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !options.TryAdd(arguments[index], arguments[index + 1]))
            {
                return false;
            }
        }
        if (!options.TryGetValue("--report", out var path)
            || !Uri.TryCreate(options.GetValueOrDefault("--api-url"), UriKind.Absolute, out var origin)
            || !(origin.Scheme == "https" || origin is { Scheme: "http", IsLoopback: true })
            || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0
            || origin.AbsolutePath != "/" || !Guid.TryParse(options.GetValueOrDefault("--project"), out var project)
            || project == Guid.Empty)
        {
            return false;
        }
        report = path;
        endpoint = new Uri(origin, $"/api/v1/projects/{project}/ci-reports");
        return true;
    }
}

internal sealed class PublishException(string message) : Exception(message);
