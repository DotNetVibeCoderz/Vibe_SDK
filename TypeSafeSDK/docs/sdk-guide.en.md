# SDK Usage Guide

## 1. Core concepts

TypeSafe answers one or more named questions about a `state`. The state can be text, an object, or an array. `Choice.Create` describes allowed choices.

```csharp
var result = await client.SystemOneAsync(
    new { document = ticketText, customerId = "C-1001" },
    Choice.Create("billing", "technical", "other"));
```

## 2. Customer support ticket classification

```csharp
static async Task<string> ClassifyAsync(string ticket)
{
    await using var client = new TypeSafeClient(
        new TypeSafeOptions { Simulator = true });
    var response = await client.SystemOneAsync(
        new { document = ticket },
        Choice.Create("billing", "technical", "other"));
    return response.Choices?["category"].Choice ?? "other";
}
```

Use case: route a ticket to billing, technical support, or general support.

## 3. Persisting a result

```csharp
var response = await client.SystemOneAsync(
    new { document = ticket.Body, ticketId = ticket.Id },
    Choice.Create("billing", "technical", "other"));

var record = new TicketClassification
{
    TicketId = ticket.Id,
    Category = response.Choices?["category"].Choice ?? "other",
    ClassifiedAtUtc = DateTimeOffset.UtcNow
};
await db.TicketClassifications.AddAsync(record);
await db.SaveChangesAsync();
```

## 4. Batch processing with cancellation

```csharp
await Parallel.ForEachAsync(tickets,
    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
    async (ticket, token) =>
    {
        var result = await client.SystemOneAsync(
            new { document = ticket.Text },
            Choice.Create("billing", "technical", "other"), token);
        Console.WriteLine($"{ticket.Id}: {result.Choices?["category"].Choice}");
    });
```

For live API calls, add rate limiting and retry/backoff in your application layer. The simulator is ideal for deterministic CI tests.

## 5. Dependency injection in ASP.NET Core

```csharp
builder.Services.AddSingleton(sp =>
    new TypeSafeClient(TypeSafeOptions.FromEnvironment()));

app.MapPost("/classify", async (TicketRequest request,
    TypeSafeClient client, CancellationToken ct) =>
{
    return await client.SystemOneAsync(
        new { document = request.Document },
        Choice.Create("billing", "technical", "other"), ct);
});
```

## 6. Custom HTTP client and testing

```csharp
var httpClient = new HttpClient(new MockHandler());
await using var client = new TypeSafeClient(
    new TypeSafeOptions { ApiKey = "test", Endpoint = "https://test.local" },
    httpClient);
```

Use a fake `HttpMessageHandler` to test payloads without calling the real API.

## 7. Error handling

Every HTTP failure raises a typed exception. `TypeSafeApiException` is the base class, so one `catch` still covers all of them, and each carries `StatusCode`, `Body`, `Headers`, `Endpoint`, and `RequestId`.

```csharp
try
{
    var result = await client.SystemOneAsync(state, questions, cancellationToken: ct);
}
catch (TypeSafeAuthenticationException)
{
    // 401 — the key is wrong or missing. Retrying will not help.
}
catch (TypeSafeRateLimitException ex)
{
    // 429 — the server told us how long to wait.
    await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(1), ct);
}
catch (TypeSafeUnprocessableEntityException ex)
{
    logger.LogError("TypeSafe rejected the payload: {Body} (request {RequestId})", ex.Body, ex.RequestId);
}
catch (TypeSafeApiTimeoutException ex)
{
    logger.LogWarning("Timed out after {Timeout}", ex.Timeout);
}
catch (TypeSafeApiConnectionException)
{
    // No response at all: DNS, TLS, or the network.
}
catch (TypeSafeApiException ex)
{
    // Anything else the API returned.
    logger.LogError("HTTP {Status} from {Endpoint}", ex.StatusCode, ex.Endpoint);
}
```

The full status-to-exception table is in [Parity with the Python SDK](parity-python.en.md#errors).

## 8. Logging

```csharp
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger<TypeSafeClient>();
await using var client = new TypeSafeClient(
    TypeSafeOptions.FromEnvironment(), logger: logger);
```

Never log API keys or sensitive customer content.

## 9. Health check and fallback

```csharp
async Task<string> ClassifyWithFallback(string text, CancellationToken ct)
{
    try
    {
        var result = await client.SystemOneAsync(
            new { document = text },
            Choice.Create("billing", "technical", "other"), ct);
        return result.Choices?["category"].Choice ?? "other";
    }
    catch (TypeSafeApiException)
    {
        return "manual-review";
    }
}
```

## 10. Additional use cases

- **Email triage:** classify incoming email before creating a ticket.
- **Fraud review:** choose `approve`, `review`, or `reject` for an internal workflow.
- **Survey routing:** classify survey sentiment or topic.
- **Document workflow:** choose `invoice`, `contract`, or `identity` before OCR processing.
- **Moderation queue:** route content to `safe`, `review`, or `blocked`.
- **IoT operations:** classify an alarm as `critical`, `warning`, or `info`.

Domain-specific choices example:

```csharp
var result = await client.SystemOneAsync(
    new { alarm = "Pump temperature exceeded threshold" },
    Choice.Create("critical", "warning", "info"));
```

## 11. Retry, timeout, and headers

Defaults match the Python SDK: two retries, 0.5 s initial backoff doubling to a 5 s ceiling, 25% jitter, retrying 408, 429, and every 5xx, honouring `Retry-After`, inside a 30 s budget for the whole call.

```csharp
var options = new TypeSafeOptions
{
    ApiKey = key,
    Timeout = TimeSpan.FromSeconds(20),
    Retry = new RetryPolicy(MaxRetries: 4, BackoffInitial: TimeSpan.FromMilliseconds(250)),
    Headers = { ["X-Tenant"] = "gravicode" }
};
await using var client = new TypeSafeClient(options);
```

Anything set on the client can be overridden for a single call:

```csharp
var result = await client.SystemOneAsync(
    state, questions,
    model: "jev-preview",
    retry: new RetryPolicy(0),                 // do not retry this one
    timeout: TimeSpan.FromSeconds(60),
    extraHeaders: new Dictionary<string, string> { ["X-Trace"] = traceId },
    extraBody: new Dictionary<string, object?> { ["metadata"] = new { tenant = "acme" } },
    cancellationToken: ct);
```

Every request carries `X-TypeSafe-SDK` and `X-TypeSafe-Runtime`; a retried attempt also carries `X-TypeSafe-Retry-Count`. Use `TypeSafeConstants.RedactHeader` before writing any header to a log — it masks `Authorization`, `x-api-key`, cookies, and the rest of `TypeSafeConstants.SecretHeaders`.

## 12. Listing models

```csharp
var models = await client.Models.ListAsync(cancellationToken: ct);
foreach (var model in models)
    Console.WriteLine($"{model.Name} — {model.Description} ({model.ReleaseDate})");
```

`ListModelsResponse` is usable directly as a list and also exposes `.Models`. At the time of writing the live API returns `jev-latest` and `jev-preview`.

## 13. Correlating requests

`SystemOneResponse.RequestId` carries the `x-typesafe-request-id` header, and every `TypeSafeApiException` carries it too. Log it on both paths and support can trace a single call.

```csharp
var response = await client.SystemOneAsync(state, questions, cancellationToken: ct);
logger.LogInformation("Classified {Ticket} as {Category} (request {RequestId})",
    ticket.Id, response.Choices["category"].Choice, response.RequestId);
```

## 14. The simulator

`Simulator = true` answers `choice`, `noul`, and `score` questions locally, with no key and no network, so unit tests and CI are deterministic.

```csharp
await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
```

Know what it is before you trust it. The simulator is **lexical, not semantic**: it matches the words in your criteria against the words in your state, weights a word that appears in only one criterion above one that appears in all of them, ignores negated mentions (`no nesting` does not count as nesting), and — when it finds no signal at all — returns a flat distribution with low confidence instead of guessing. It is the right tool for asserting that your plumbing works and your schemas are shaped correctly. It is not a stand-in for the model's judgement: on the same quest description the simulator answers `steady` where the live model answers `demanding`.

Give your criteria real descriptions and both the simulator and the live model do better:

```csharp
["state"] = Choice.Create(new Dictionary<string, string?>
{
    ["nominal"]   = "Readings steady and within limits, no trend",
    ["degrading"] = "Readings drifting: pressure falling or temperature rising over time",
    ["fault"]     = "Readings breach safe limits or an alarm is active",
    ["offline"]   = "No telemetry received from the asset"
}, "What state is this asset in?")
```

## 15. Best practices

1. Use the simulator for unit tests and CI.
2. Store API keys in a secret manager or environment variable.
3. Limit concurrency during batch processing.
4. Validate input before sending data.
5. Do not send unnecessary PII.
6. Log `RequestId` on both success and failure so a call can be traced end to end.
7. Catch the specific exception you can act on; let `TypeSafeApiException` catch the rest.
8. Describe your criteria — a label plus a sentence beats a bare label.

Created by **Gravicode Studios**, led by Kang Fadhil.
