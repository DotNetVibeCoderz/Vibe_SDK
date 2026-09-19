using System.Net;
using System.Net.Http.Json;
using TypeSafeSdk;

namespace TypeSafeSdk.Tests;
public class ApiContractTests
{
    [Fact]
    public async Task Client_UsesPythonSdkPathHeadersAndPayload()
    {
        var handler = new StubHandler();
        await using var client = new TypeSafeClient(new TypeSafeOptions { ApiKey = "secret", Endpoint = "https://example.test/" }, new HttpClient(handler));
        var response = await client.SystemOneAsync(new { document = "hello" }, Choice.Create("other"));
        Assert.Equal("other", response.Choices["category"].Choice);
        Assert.Equal("Bearer secret", handler.Authorization);
        Assert.Equal("https://example.test/v1/systemone", handler.RequestUri!.AbsoluteUri);
        Assert.Contains("\"state\"", handler.Body); Assert.Contains("\"questions\"", handler.Body); Assert.Contains("\"model\"", handler.Body);
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; } public Uri? RequestUri { get; private set; } public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Authorization = request.Headers.Authorization?.ToString(); RequestUri = request.RequestUri; Body = await request.Content!.ReadAsStringAsync(cancellationToken); return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new { category = new { choice = "other", confidence = 1.0 } } }) }; }
    }
}
