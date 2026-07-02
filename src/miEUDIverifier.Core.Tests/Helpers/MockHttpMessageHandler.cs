using System.Net;

namespace miEUDIverifier.Tests.Helpers;

/// <summary>
/// Ersetzte den echten HTTP-Transport durch eine konfigurierbare Lambda-Funktion.
/// Wird in Tests verwendet um Netzwerkaufrufe zu simulieren ohne echte HTTP-Verbindungen.
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
/// Hilfsmethoden für typsichere JSON-Antworten in Tests.
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
