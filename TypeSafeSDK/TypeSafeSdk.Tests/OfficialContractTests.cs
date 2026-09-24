using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TypeSafeSdk;
namespace TypeSafeSdk.Tests;
public sealed class OfficialContractTests
{
    [Fact] public void Choice_Noul_Score_CreateCorrectQuestions() { var choice = JsonSerializer.Serialize(Choice.Create("a", "b").ToApiQuestion()); var noul = JsonSerializer.Serialize(new NoulSchema("Is urgent?").ToApiQuestion()); var score = JsonSerializer.Serialize(new ScoreSchema("How urgent?", "low", "high").ToApiQuestion()); Assert.Contains("\"type\":\"choice\"", choice); Assert.Contains("\"type\":\"noul\"", noul); Assert.Contains("\"type\":\"score\"", score); }
    [Fact] public void Response_ParsesAllOfficialAnswerTypes() { var json = """{"model":"jev-latest","answers":{"urgent":{"type":"noul","noul":0.92},"department":{"type":"choice","choice":"technical","probabilities":{"billing":0.1,"technical":0.9},"confidence":0.85},"frustration":{"type":"score","score":1.6,"legend":{"0":"Calm","1":"Frustrated","2":"Angry"},"probabilities":{"0":0.05,"1":0.3,"2":0.65},"confidence":0.78}},"usage":{"input_tokens":312,"output_tokens":48}}"""; var response = SystemOneResponse.Parse(json); Assert.Equal("jev-latest", response.Model); Assert.Equal(0.92, response.Nouls["urgent"].Noul); Assert.Equal("technical", response.Choices["department"].Choice); Assert.Equal(1.6, response.Scores["frustration"].Score); Assert.Equal(312, response.Usage!.InputTokens); }
    [Fact] public async Task Client_SendsOfficialPayloadAndModelsEndpoint() { var handler = new ContractHandler(); await using var client = new TypeSafeClient(new TypeSafeOptions { ApiKey = "secret", Endpoint = "https://example.test" }, new HttpClient(handler)); await client.SystemOneAsync("hello", new Dictionary<string, object> { ["urgent"] = new NoulSchema("Is urgent?").ToApiQuestion() }, "jev-latest", cancellationToken: default); Assert.Equal("https://example.test/v1/systemone", handler.LastUri!.AbsoluteUri); Assert.Contains("\"state\":\"hello\"", handler.Body); Assert.Contains("\"model\":\"jev-latest\"", handler.Body); Assert.Contains("\"questions\"", handler.Body); await client.Models.ListAsync(); Assert.Equal("https://example.test/v1/models", handler.LastUri.AbsoluteUri); }
    [Theory]
    [InlineData(400, typeof(TypeSafeBadRequestException))]
    [InlineData(401, typeof(TypeSafeAuthenticationException))]
    [InlineData(403, typeof(TypeSafePermissionDeniedException))]
    [InlineData(404, typeof(TypeSafeNotFoundException))]
    [InlineData(422, typeof(TypeSafeUnprocessableEntityException))]
    [InlineData(429, typeof(TypeSafeRateLimitException))]
    [InlineData(529, typeof(TypeSafeInternalServerException))]
    public async Task Client_MapsOfficialErrors(int status, Type expected)
    {
        var handler = new ContractHandler { Status = status, SucceedAfter = int.MaxValue };
        await using var client = new TypeSafeClient(new TypeSafeOptions { ApiKey = "secret", Endpoint = "https://example.test", Retry = new RetryPolicy(0) }, new HttpClient(handler));
        var error = await Assert.ThrowsAnyAsync<TypeSafeApiException>(() => client.SystemOneAsync("hello", Choice.Create("a", "b")));
        Assert.IsType(expected, error);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("https://example.test/v1/systemone", error.Endpoint);
    }
    [Fact] public async Task RetryPolicy_Retries429() { var handler = new ContractHandler { Status = 429, SucceedAfter = 2 }; await using var client = new TypeSafeClient(new TypeSafeOptions { ApiKey = "secret", Endpoint = "https://example.test" }, new HttpClient(handler)); var result = await client.SystemOneAsync("hello", Choice.Create("a", "b"), retry: new RetryPolicy(2, TimeSpan.FromMilliseconds(1))); Assert.Equal("a", result.Choices["category"].Choice); Assert.Equal(2, handler.Attempts); }
    private sealed class ContractHandler : HttpMessageHandler { public int Status { get; set; } = 200; public int SucceedAfter { get; set; } = 1; public int Attempts { get; private set; } public Uri? LastUri { get; private set; } public string Body { get; private set; } = ""; protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Attempts++; LastUri = request.RequestUri; Body = await (request.Content?.ReadAsStringAsync(token) ?? Task.FromResult("")); if (Attempts < SucceedAfter) return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent("rate limited") }; if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { name = "jev-latest", description = "default" } }) }; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"model\":\"jev-latest\",\"answers\":{\"category\":{\"type\":\"choice\",\"choice\":\"a\",\"probabilities\":{\"a\":1},\"confidence\":1}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}") }; } }
}
