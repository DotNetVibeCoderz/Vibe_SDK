# TypeSafe .NET SDK

Unofficial, strongly typed .NET 10 client for the TypeSafe **System One** API, ported from the [official Python SDK](https://github.com/typesafe-ai/typesafe-sdk-python).

```bash
dotnet add package Gravicode.TypeSafeSdk
```

## Ask a question about some text

```csharp
using TypeSafeSdk;

await using var client = new TypeSafeClient(TypeSafeOptions.FromEnvironment());

var response = await client.SystemOneAsync(
    new { document = "I was charged twice. Please fix this ASAP." },
    Choice.Create("billing", "technical", "other"));

Console.WriteLine(response.Choices["category"].Choice);      // billing
Console.WriteLine(response.Choices["category"].Confidence);  // 1
```

## Three kinds of question, one request

```csharp
var response = await client.SystemOneAsync(
    new { document = ticket.Body },
    new Dictionary<string, object>
    {
        ["category"] = Choice.Create("billing", "technical", "other"),
        ["urgent"]   = new NoulSchema("Does this need a response within one hour?"),
        ["tone"]     = new ScoreSchema("How frustrated is the customer?", "calm", "annoyed", "angry")
    });

var queue    = response.Choices["category"].Choice;       // a label
var urgent   = response.Nouls["urgent"].IsTrue;           // a 0..1 value, thresholded
var tone     = response.Scores["tone"].NearestLabel;      // an expected value on a scale
```

Every answer carries its full probability distribution, so an ambiguous case is visible instead of hidden behind a single label.

## Typed failures

`TypeSafeApiException` is the base for every HTTP failure, and each one carries `StatusCode`, `Body`, `Headers`, `Endpoint`, and `RequestId`.

```csharp
catch (TypeSafeRateLimitException ex) { await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(1)); }
catch (TypeSafeAuthenticationException) { /* 401 — retrying will not help */ }
catch (TypeSafeApiTimeoutException ex) { logger.LogWarning("Timed out after {Timeout}", ex.Timeout); }
```

Also available: `TypeSafeBadRequestException`, `TypeSafePermissionDeniedException`, `TypeSafeNotFoundException`, `TypeSafeUnprocessableEntityException`, `TypeSafeInternalServerException`, `TypeSafeApiConnectionException`, and `TypeSafeApiResponseValidationException`.

## Retry, timeout, headers

Defaults match the Python SDK: two retries, 0.5 s backoff doubling to a 5 s ceiling with 25% jitter, retrying 408/429/5xx, honouring `Retry-After`, inside a 30 s budget.

```csharp
var options = new TypeSafeOptions
{
    Timeout = TimeSpan.FromSeconds(20),
    Retry   = new RetryPolicy(MaxRetries: 4),
    Headers = { ["X-Tenant"] = "acme" }
};
```

Any of it can be overridden per call, alongside `extraHeaders` and `extraBody`.

## Tests without a key

```csharp
await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
```

The simulator answers `choice`, `noul`, and `score` questions locally and deterministically — no key, no network. It is lexical rather than semantic: it weights rare criteria words above common ones, honours negation, and abstains with a flat distribution when it finds no signal instead of guessing.

## Configuration

`TypeSafeOptions.FromEnvironment()` reads `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL`, `TYPESAFE_LOG_LEVEL`, and `TYPESAFE_SIMULATOR`. The names are also constants on `TypeSafeConstants`.

## Links

- [Full documentation](https://github.com/DotNetVibeCoderz/Vibe_SDK/tree/main/TypeSafeSDK/docs)
- [Parity with the Python SDK](https://github.com/DotNetVibeCoderz/Vibe_SDK/blob/main/TypeSafeSDK/docs/parity-python.en.md)

Created by **Gravicode Studios**, led by Kang Fadhil. AI assistant: **Jack — The Code Bender**.
