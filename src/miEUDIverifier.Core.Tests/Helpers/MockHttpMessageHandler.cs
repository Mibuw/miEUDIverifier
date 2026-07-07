using System.Net;

namespace miEUDIverifier.Tests.Helpers;

/// <summary>
/// Replaces the real HTTP transport with a configurable lambda function.
/// Used in tests to simulate network calls without real HTTP connections.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_respond(request));
}

/// <summary>
/// Helper methods for type-safe JSON responses in tests.
/// </summary>
public static class HttpResponseFactory
{
    public static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage BadRequest(string json) =>
        new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound);
}
