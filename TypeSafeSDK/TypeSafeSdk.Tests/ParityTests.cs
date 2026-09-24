using System.Net;
using System.Text.Json;
using TypeSafeSdk;

namespace TypeSafeSdk.Tests;

/// <summary>Menguji fitur yang disejajarkan dengan Python SDK: error bertipe, retry, timeout, header, dan simulator generik.</summary>
public sealed class ParityTests
{
    [Fact]
    public async Task ApiError_ExposesRequestIdAndRetryAfter()
    {
        var handler = ScriptedHandler.Sync((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") };
            response.Headers.TryAddWithoutValidation(TypeSafeConstants.RequestIdHeader, "req-42");
            response.Headers.TryAddWithoutValidation(TypeSafeConstants.RetryAfterMsHeader, "1500");
            return response;
        });
        await using var client = new TypeSafeClient(Options(), new HttpClient(handler));
        var error = await Assert.ThrowsAsync<TypeSafeRateLimitException>(() => client.SystemOneAsync("hello", Choice.Create("a", "b")));
        Assert.Equal("req-42", error.RequestId);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), error.RetryAfter);
        Assert.Equal("slow down", error.Body);
    }

    [Fact]
    public async Task Timeout_ThrowsTypedTimeoutError()
    {
        var handler = new ScriptedHandler(async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(5), token); return new HttpResponseMessage(HttpStatusCode.OK); });
        await using var client = new TypeSafeClient(Options(o => { o.Timeout = TimeSpan.FromMilliseconds(40); o.Retry = new RetryPolicy(0); }), new HttpClient(handler));
        var error = await Assert.ThrowsAsync<TypeSafeApiTimeoutException>(() => client.SystemOneAsync("hello", Choice.Create("a", "b")));
        Assert.Equal(TimeSpan.FromMilliseconds(40), error.Timeout);
    }

    [Fact]
    public async Task ConnectionFailure_ThrowsTypedConnectionError()
    {
        var handler = ScriptedHandler.Sync((_, _) => throw new HttpRequestException("dns failure"));
        await using var client = new TypeSafeClient(Options(o => o.Retry = new RetryPolicy(0)), new HttpClient(handler));
        var error = await Assert.ThrowsAsync<TypeSafeApiConnectionException>(() => client.SystemOneAsync("hello", Choice.Create("a", "b")));
        Assert.Contains("dns failure", error.Message);
    }

    [Fact]
    public async Task MissingApiKey_ThrowsAuthenticationError()
    {
        await using var client = new TypeSafeClient(new TypeSafeOptions { ApiKey = "", Endpoint = "https://example.test" });
        await Assert.ThrowsAsync<TypeSafeAuthenticationException>(() => client.SystemOneAsync("hello", Choice.Create("a", "b")));
    }

    [Fact]
    public async Task Request_CarriesIdentityHeadersExtraHeadersAndExtraBody()
    {
        var handler = ScriptedHandler.Sync((_, _) => Ok());
        await using var client = new TypeSafeClient(Options(o => o.Headers["X-Tenant"] = "gravicode"), new HttpClient(handler));
        await client.SystemOneAsync("hello", Choice.Create("a", "b"), extraHeaders: new Dictionary<string, string> { ["X-Trace"] = "abc" },
            extraBody: new Dictionary<string, object?> { ["metadata"] = new { source = "unit-test" } });

        Assert.Equal(TypeSafeConstants.SdkHeaderValue, handler.LastHeaders[TypeSafeConstants.SdkHeader]);
        Assert.StartsWith(".NET/", handler.LastHeaders[TypeSafeConstants.RuntimeHeader]);
        Assert.Equal("gravicode", handler.LastHeaders["X-Tenant"]);
        Assert.Equal("abc", handler.LastHeaders["X-Trace"]);
        Assert.Contains("\"metadata\"", handler.LastBody);
        Assert.Contains("\"unit-test\"", handler.LastBody);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Retry_CoversOfficialTransientStatuses(int status)
    {
        var attempts = 0;
        var handler = ScriptedHandler.Sync((_, _) => ++attempts < 2 ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("transient") } : Ok());
        await using var client = new TypeSafeClient(Options(o => o.Retry = new RetryPolicy(2, TimeSpan.FromMilliseconds(1))), new HttpClient(handler));
        var result = await client.SystemOneAsync("hello", Choice.Create("a", "b"));
        Assert.Equal(2, attempts);
        Assert.Equal("a", result.Choices["category"].Choice);
    }

    [Fact]
    public async Task Retry_SendsRetryCountHeader()
    {
        var attempts = 0;
        var handler = ScriptedHandler.Sync((_, _) => ++attempts < 2 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow") } : Ok());
        await using var client = new TypeSafeClient(Options(o => o.Retry = new RetryPolicy(2, TimeSpan.FromMilliseconds(1))), new HttpClient(handler));
        await client.SystemOneAsync("hello", Choice.Create("a", "b"));
        Assert.Equal("1", handler.LastHeaders[TypeSafeConstants.RetryCountHeader]);
    }

    [Fact]
    public void RetryPolicy_HonoursRetryAfterAndCapsBackoff()
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), BackoffJitter: 0);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextDelay(9));
        Assert.Equal(TimeSpan.FromMilliseconds(300), policy.NextDelay(0, TimeSpan.FromMilliseconds(300)));
        Assert.True(policy.ShouldRetryStatus(408) && policy.ShouldRetryStatus(429) && policy.ShouldRetryStatus(503));
        Assert.False(policy.ShouldRetryStatus(400));
    }

    [Fact]
    public void RetryPolicy_JitterStaysWithinBounds()
    {
        var policy = new RetryPolicy(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), BackoffJitter: 0.25);
        for (var i = 0; i < 50; i++)
        {
            var delay = policy.NextDelay(0);
            Assert.InRange(delay.TotalMilliseconds, 750, 1000);
        }
    }

    [Fact]
    public void QuestionPayload_NormalizesSchemasIntoWireShape()
    {
        var payload = QuestionPayload.Normalize(new Dictionary<string, object>
        {
            ["urgent"] = new NoulSchema("Is this urgent?", "Needs action today", "Can wait"),
            ["department"] = Choice.Create("billing", "technical"),
            ["severity"] = new ScoreSchema("How severe?", "low", "high")
        });
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("\"type\":\"noul\"", json);
        Assert.Contains("\"type\":\"choice\"", json);
        Assert.Contains("\"type\":\"score\"", json);
        Assert.DoesNotContain("\"Values\"", json);
    }

    [Fact]
    public async Task RawSchema_IsNormalizedBeforeSending()
    {
        var handler = ScriptedHandler.Sync((_, _) => Ok());
        await using var client = new TypeSafeClient(Options(), new HttpClient(handler));
        await client.SystemOneAsync("hello", new Dictionary<string, object> { ["category"] = Choice.Create("a", "b") });
        Assert.Contains("\"type\":\"choice\"", handler.LastBody);
        Assert.DoesNotContain("\"Values\"", handler.LastBody);
    }

    [Fact]
    public void Choice_SupportsCriteriaDescriptions()
    {
        var schema = Choice.Create(new Dictionary<string, string?> { ["billing"] = "Payments and invoices", ["technical"] = null }, "Which queue?");
        var json = JsonSerializer.Serialize(schema.ToApiQuestion());
        Assert.Contains("\"instructions\":\"Which queue?\"", json);
        Assert.Contains("\"billing\":\"Payments and invoices\"", json);
        Assert.Contains("\"technical\":null", json);
    }

    [Fact]
    public void Simulator_AnswersChoiceNoulAndScore()
    {
        var response = TypeSafeSimulator.Answer(
            new { document = "The deployment crashed with a fatal error and needs an urgent fix" },
            QuestionPayload.Normalize(new Dictionary<string, object>
            {
                ["department"] = Choice.Create("billing", "technical", "other"),
                ["urgent"] = new NoulSchema("Is this urgent?", "urgent critical emergency"),
                ["severity"] = new ScoreSchema("How severe?", "calm", "elevated", "critical")
            }));

        Assert.Equal("technical", response.Choices["department"].Choice);
        Assert.True(response.Nouls["urgent"].IsTrue);
        Assert.Equal(3, response.Scores["severity"].Legend.Count);
        Assert.InRange(response.Scores["severity"].Score, 0, 2);
        Assert.Equal(TypeSafeConstants.DefaultModel, response.Model);
        Assert.NotNull(response.Usage);
    }

    [Fact]
    public void Simulator_IgnoresWordsSharedByEveryCriterion()
    {
        // Every description below starts with "Player", so that token must not decide anything;
        // only "barter"/"goods", which appear in a single criterion, may move the answer.
        var response = TypeSafeSimulator.Answer(
            new { player_line = "I want to barter these goods", npc = "Blacksmith" },
            QuestionPayload.Normalize(new Dictionary<string, object>
            {
                ["intent"] = Choice.Create(new Dictionary<string, string?>
                {
                    ["trade"] = "Player wants to buy, sell, or barter goods",
                    ["quest"] = "Player asks about a task, objective, or rumour",
                    ["lore"] = "Player asks about history, people, or places",
                    ["farewell"] = "Player is ending the conversation"
                }, "Which dialogue handler should answer this line?")
            }));

        var probabilities = response.ChoiceAnswers["intent"].Probabilities;
        Assert.Equal("trade", response.Choices["intent"].Choice);
        Assert.True(probabilities.Values.Max() - probabilities.Values.Min() > 0.1,
            $"Distribution was flat: {string.Join(", ", probabilities.Select(p => $"{p.Key}={p.Value}"))}");
    }

    [Fact]
    public void Simulator_IgnoresNegatedMentions()
    {
        var questions = QuestionPayload.Normalize(new Dictionary<string, object>
        {
            ["behaviour"] = Choice.Create(new Dictionary<string, string?>
            {
                ["feeding"] = "Foraging or feeding in water",
                ["nesting"] = "Nesting or tending young"
            }, "What behaviour was recorded?")
        });

        var affirmed = TypeSafeSimulator.Answer(new { note = "herons nesting on the far bank" }, questions);
        var negated = TypeSafeSimulator.Answer(new { note = "herons feeding in the shallows, no nesting observed" }, questions);

        Assert.Equal("nesting", affirmed.Choices["behaviour"].Choice);
        Assert.Equal("feeding", negated.Choices["behaviour"].Choice);
    }

    [Fact]
    public void Simulator_ScoresValuesNotFieldNames()
    {
        // "billing" appears only as a field NAME here, so it must not pull the answer to billing.
        var response = TypeSafeSimulator.Answer(
            new { billing = "the application crashed with a fatal error" },
            QuestionPayload.Normalize(new Dictionary<string, object> { ["category"] = Choice.Create("billing", "technical", "other") }));
        Assert.Equal("technical", response.Choices["category"].Choice);
    }

    [Fact]
    public void Simulator_ProbabilitiesSumToOne()
    {
        var response = TypeSafeSimulator.Classify("I was charged twice for my subscription");
        var probabilities = response.ChoiceAnswers["category"].Probabilities;
        Assert.Equal(3, probabilities.Count);
        Assert.Equal(1.0, probabilities.Values.Sum(), 2);
    }

    [Fact]
    public void Simulator_IsDeterministic()
    {
        var first = TypeSafeSimulator.Classify("payment failed on invoice 900");
        var second = TypeSafeSimulator.Classify("payment failed on invoice 900");
        Assert.Equal(first.Choices["category"].Choice, second.Choices["category"].Choice);
        Assert.Equal(first.Choices["category"].Confidence, second.Choices["category"].Confidence);
    }

    [Fact]
    public void ListModelsResponse_ParsesBothWireShapes()
    {
        var bare = ListModelsResponse.Parse("""[{"name":"jev-latest","description":"default","release_date":"2026-01-15"}]""");
        var wrapped = ListModelsResponse.Parse("""{"models":[{"name":"jev-latest","description":"default","release_date":"2026-01-15"}]}""");
        Assert.Single(bare);
        Assert.Equal("jev-latest", wrapped[0].Name);
        Assert.Equal("2026-01-15", wrapped[0].ReleaseDate);
    }

    [Fact]
    public async Task Models_UsesSimulatorWithoutNetwork()
    {
        await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
        var models = await client.Models.ListAsync();
        Assert.Equal(TypeSafeConstants.DefaultModel, models[0].Name);
    }

    [Fact]
    public void Usage_TreatsMissingCountsAsNull()
    {
        var response = SystemOneResponse.Parse("""{"model":"jev-latest","answers":{},"usage":{"input_tokens":10}}""");
        Assert.Equal(10, response.Usage!.InputTokens);
        Assert.Null(response.Usage.OutputTokens);
        Assert.Equal(10, response.Usage.TotalTokens);
    }

    [Fact]
    public void ScoreAnswer_ExposesLegendByLevel()
    {
        var response = SystemOneResponse.Parse("""{"answers":{"tone":{"type":"score","score":1.8,"legend":{"0":"Calm","1":"Tense","2":"Angry"},"probabilities":{"2":1.0},"confidence":0.9}}}""");
        var score = response.Scores["tone"];
        Assert.Equal("Angry", score.NearestLabel);
        Assert.Equal(3, score.LegendByLevel.Count);
    }

    [Fact]
    public void Options_ReadOfficialEnvironmentNames()
    {
        Environment.SetEnvironmentVariable(TypeSafeConstants.BaseUrlEnv, "https://env.test");
        Environment.SetEnvironmentVariable(TypeSafeConstants.LogLevelEnv, "Warning");
        try
        {
            var options = TypeSafeOptions.FromEnvironment();
            Assert.Equal("https://env.test", options.Endpoint);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, options.LogLevel);
            Assert.Equal(TypeSafeConstants.DefaultTimeout, options.Timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TypeSafeConstants.BaseUrlEnv, null);
            Environment.SetEnvironmentVariable(TypeSafeConstants.LogLevelEnv, null);
        }
    }

    [Fact]
    public void Constants_MatchPythonSdk()
    {
        Assert.Equal("TYPESAFE_API_KEY", TypeSafeConstants.ApiKeyEnv);
        Assert.Equal("TYPESAFE_BASE_URL", TypeSafeConstants.BaseUrlEnv);
        Assert.Equal("TYPESAFE_DEFAULT_MODEL", TypeSafeConstants.DefaultModelEnv);
        Assert.Equal("TYPESAFE_LOG_LEVEL", TypeSafeConstants.LogLevelEnv);
        Assert.Equal("https://api.typesafe.ai", TypeSafeConstants.DefaultBaseUrl);
        Assert.Equal("jev-latest", TypeSafeConstants.DefaultModel);
        Assert.Equal(TimeSpan.FromSeconds(10), TypeSafeConstants.DefaultTimeout);
    }

    [Fact]
    public void Constants_RedactSecretHeaders()
    {
        Assert.Equal("***", TypeSafeConstants.RedactHeader("Authorization", "Bearer secret"));
        Assert.Equal("gravicode", TypeSafeConstants.RedactHeader("X-Tenant", "gravicode"));
    }

    [Fact]
    public void ResponseValidationError_CarriesFieldPath()
    {
        var error = new TypeSafeApiResponseValidationException("answers.tone.confidence", "{}");
        Assert.Equal("answers.tone.confidence", error.FieldPath);
        Assert.Contains("answers.tone.confidence", error.Message);
    }

    private static TypeSafeOptions Options(Action<TypeSafeOptions>? configure = null)
    {
        var options = new TypeSafeOptions { ApiKey = "secret", Endpoint = "https://example.test", Retry = new RetryPolicy(0) };
        configure?.Invoke(options);
        return options;
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"model":"jev-latest","answers":{"category":{"type":"choice","choice":"a","probabilities":{"a":1},"confidence":1}},"usage":{"input_tokens":1,"output_tokens":1}}""")
    };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public static ScriptedHandler Sync(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
            => new((request, token) => Task.FromResult(respond(request, token)));

        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastHeaders.Clear();
            foreach (var header in request.Headers) LastHeaders[header.Key] = string.Join(", ", header.Value);
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request, cancellationToken);
        }
    }
}
