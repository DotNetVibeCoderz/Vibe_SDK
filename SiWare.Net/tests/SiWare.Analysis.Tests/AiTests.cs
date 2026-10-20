using System.Net;
using System.Text.Json;
using SiWare.Ai;
using SiWare.Analysis;
using SiWare.Sdk;

namespace SiWare.Analysis.Tests;

public class AiTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static readonly ChatCompletionsOptions Options = new(new Uri("https://llm.example/v1/"), "test-model", "secret-key", "test");

    private static async Task<Spectrum> SimulatedAbsorbance()
    {
        await using var device = new SimulatedSpectrometer();
        await device.ConnectAsync();
        var scan = new ScanOptions(IntegrationMilliseconds: 1);
        var dark = await device.AcquireAsync(MeasurementKind.Dark, scan);
        var reference = await device.AcquireAsync(MeasurementKind.Reference, scan);
        return SpectralProcessing.Absorbance(await device.AcquireAsync(MeasurementKind.Sample, scan), dark, reference);
    }

    [Fact]
    public async Task Facts_FindSimulatorBandsAndStateProvenance()
    {
        var facts = SpectrumFacts.Describe(await SimulatedAbsorbance());
        Assert.Contains("SIMULATED", facts);
        // The simulator places absorption bands at 1450, 1940, and 2310 nm; the 257-point grid samples every ~4.7 nm.
        var peaks = System.Text.RegularExpressions.Regex.Matches(facts, @"(\d{4}) nm \(value").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        foreach (var band in new[] { 1450, 1940, 2310 })
            Assert.Contains(peaks, peak => Math.Abs(peak - band) <= 10);
    }

    [Fact]
    public void Facts_UseInvariantCultureForNumbers()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("id-ID");
            var facts = SpectrumFacts.Describe(PcaResult.Fit(SimulatedSampleSet.Generate(samples: 10, points: 32), 2));
            Assert.Matches(@"\d\.\d %", facts);
            Assert.DoesNotMatch(@"\d,\d", facts);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public async Task ChatCompletions_SendsBearerTokenModelAndFactsOnly()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"model":"served-model","choices":[{"message":{"role":"assistant","content":"<think>scratch</think>Likely water bands."}}]}""");
        var result = await new ChatCompletionsInterpreter(new HttpClient(handler), Options).InterpretAsync("Facts line", "id");

        Assert.Equal("https://llm.example/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", handler.Request.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.TryGetProperty("max_tokens", out _));
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("Bahasa Indonesia", messages[0].GetProperty("content").GetString());
        Assert.EndsWith("Facts line", messages[1].GetProperty("content").GetString());

        Assert.Equal("Likely water bands.", result.Text);
        Assert.Equal("served-model", result.Model);
        Assert.True(result.IsAiGenerated);
        Assert.Equal(Interpretation.DisclaimerIndonesian, result.Disclaimer);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, """{"error":"You have no remaining credits."}""", "no remaining credits")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid token"}}""", "Invalid token")]
    [InlineData(HttpStatusCode.BadGateway, "upstream down", "upstream down")]
    public async Task ChatCompletions_SurfacesProviderErrorsWithoutTheKey(HttpStatusCode status, string body, string expected)
    {
        var interpreter = new ChatCompletionsInterpreter(new HttpClient(new FakeHandler(status, body)), Options);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => interpreter.InterpretAsync("facts"));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("secret-key", error.Message);
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_RejectsEmptyOrMalformedResponsesAndOversizedFacts()
    {
        Task Run(string body, string facts = "facts") =>
            new ChatCompletionsInterpreter(new HttpClient(new FakeHandler(HttpStatusCode.OK, body)), Options).InterpretAsync(facts);
        await Assert.ThrowsAsync<IOException>(() => Run("""{"choices":[]}"""));
        await Assert.ThrowsAsync<IOException>(() => Run("not json"));
        var exhausted = await Assert.ThrowsAsync<IOException>(() => Run("""{"choices":[{"message":{"content":""},"finish_reason":"length"}]}"""));
        Assert.Contains("raise MaxTokens", exhausted.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => Run("{}", new string('x', InterpretationPrompt.MaxFactsLength + 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => Run("{}", " "));
    }

    [Fact]
    public void Options_NeverPrintTheKey() => Assert.DoesNotContain("secret-key", Options.ToString());

    [Fact]
    public void LocalPrompt_UsesChatMlTemplate()
    {
        var prompt = LocalModelInterpreter.ChatMl("system text", "user text");
        Assert.StartsWith("<|im_start|>system\nsystem text<|im_end|>", prompt);
        Assert.EndsWith("<|im_start|>assistant\n", prompt);
    }

    /// <summary>Opt-in: set SIWARE_LIVE_LLM=1 and HF_TOKEN to call the real Hugging Face router.</summary>
    [Fact]
    public async Task Live_HuggingFaceRouter_InterpretsSimulatedSpectrum()
    {
        if (Environment.GetEnvironmentVariable("SIWARE_LIVE_LLM") != "1") return;
        using var http = new HttpClient();
        var result = await new ChatCompletionsInterpreter(http, ChatCompletionsOptions.HuggingFaceRouter())
            .InterpretAsync(SpectrumFacts.Describe(await SimulatedAbsorbance()));
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
