using System.Net;
using Guardian.Cli;
using Xunit;

namespace Guardian.Core.Tests;

public sealed class ReportPublisherTests
{
    private static readonly Uri Endpoint = new("https://guardian.example.invalid/api/v1/report");
    private const string Success = """{"analysis_id":"8ecf91c7-bb49-41e7-ba9b-f4dd556bf40a","status":"FAIL"}""";

    [Fact]
    public async Task RetriesTemporaryFailureWithTheSameBodyAndAcceptsFailReport()
    {
        using var handler = new Stub(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var result = await ReportPublisher.SendAsync(client, Endpoint, "sample-token", "{}"u8.ToArray(),
            CancellationToken.None);
        Assert.Equal(Success, result);
        Assert.Equal(["{}", "{}"], handler.Bodies);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task PermanentFailureDoesNotRetry(HttpStatusCode status)
    {
        using var handler = new Stub(status);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PublishException>(() => ReportPublisher.SendAsync(
            client, Endpoint, "sample-token", "{}"u8.ToArray(), CancellationToken.None));
        Assert.Contains($"HTTP {(int)status}", error.Message);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task UnavailableApiStopsAfterThreeAttempts()
    {
        using var handler = new Stub(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<PublishException>(() => ReportPublisher.SendAsync(
            client, Endpoint, "sample-token", "{}"u8.ToArray(), CancellationToken.None));
        Assert.Equal(3, handler.Bodies.Count);
    }

    private sealed class Stub(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("sample-token", request.Headers.Authorization?.Parameter);
            var status = statuses[Bodies.Count];
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(status) { Content = new StringContent(Success) };
        }
    }
}
