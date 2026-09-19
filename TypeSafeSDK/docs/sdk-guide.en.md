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

```csharp
try
{
    var result = await client.SystemOneAsync(state, schema, ct);
}
catch (TypeSafeApiException ex) when (ex.StatusCode == 401)
{
    // Invalid API key or insufficient access.
}
catch (TypeSafeApiException ex) when (ex.StatusCode == 429)
{
    // Rate limited; schedule an exponential-backoff retry.
}
catch (HttpRequestException)
{
    // Connectivity failure.
}
```

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

## 11. Best practices

1. Use the simulator for unit tests and CI.
2. Store API keys in a secret manager or environment variable.
3. Limit concurrency during batch processing.
4. Validate input before sending data.
5. Do not send unnecessary PII.
6. Preserve request IDs in your application observability layer when needed.

Created by **Gravicode Studios**, led by Kang Fadhil.
