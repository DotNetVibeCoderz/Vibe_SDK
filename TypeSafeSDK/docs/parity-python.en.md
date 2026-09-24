# Parity with the official Python SDK

[Bahasa Indonesia](parity-python.id.md) · **English**

This .NET SDK is an unofficial port of [`typesafe-ai/typesafe-sdk-python`](https://github.com/typesafe-ai/typesafe-sdk-python). This page records what was compared, what is now at parity, and what deliberately differs because .NET is not Python.

## Public surface, side by side

| Python SDK | .NET SDK | Status |
| --- | --- | --- |
| `TypeSafeClient` / `AsyncTypeSafeClient` | `TypeSafeClient` (async) | Adapted — see [Sync vs async](#sync-vs-async) |
| `client.system_one(state, questions, …)` | `client.SystemOneAsync(state, questions, …)` | Parity |
| `client.models.list()` | `client.Models.ListAsync()` | Parity |
| `Choice`, `Score`, `Noul` | `ChoiceSchema`, `ScoreSchema`, `NoulSchema` | Parity |
| `NoulCriteria` (`true` / `false`) | `NoulSchema(instructions, True, False)` | Parity |
| `ChoiceModel.criteria` mapping label → description | `Choice.Create(IReadOnlyDictionary<string, string?>)` | Parity |
| `Question`, `Questions`, `QuestionModel` type aliases | `IQuestionSchema`, `QuestionsSchema`, `QuestionPayload.Normalize` | Adapted |
| `SystemOneResponse` with `.nouls` / `.choices` / `.scores` | Same, plus `.ChoiceAnswers` and `.RequestId` | Parity + |
| `Answer`, `NoulAnswer`, `ChoiceAnswer`, `ScoreAnswer` | Same names, `Answer` is a polymorphic record | Parity |
| `ScoreAnswer.legend: dict[int, str]` | `Legend` (string keys) plus `LegendByLevel` and `NearestLabel` | Parity + |
| `Usage.input_tokens / output_tokens` nullable | `Usage.InputTokens / OutputTokens` (`int?`) plus `TotalTokens` | Parity + |
| `ListModelsResponse.models` | `ListModelsResponse`, also usable directly as a list | Parity |
| `ModelMetadata.name / description / release_date` | Same, `release_date` mapped explicitly | Parity |
| `RetryPolicy` | `RetryPolicy` record | Parity — see [Retry](#retry) |
| `constants` module | `TypeSafeConstants` | Parity |
| 12 typed errors | 12 typed exceptions | Parity — see [Errors](#errors) |

## Errors

The Python SDK maps status codes to specific error classes. The .NET SDK now does the same. `TypeSafeApiException` remains the base for every HTTP failure, so a single `catch` still covers all of them.

| HTTP | Python | .NET |
| --- | --- | --- |
| — | `TypeSafeError` | `TypeSafeException` |
| any | `TypeSafeAPIError` | `TypeSafeApiException` |
| 400 | `TypeSafeBadRequestError` | `TypeSafeBadRequestException` |
| 401 | `TypeSafeAuthenticationError` | `TypeSafeAuthenticationException` |
| 403 | `TypeSafePermissionDeniedError` | `TypeSafePermissionDeniedException` |
| 404 | `TypeSafeNotFoundError` | `TypeSafeNotFoundException` |
| 422 | `TypeSafeUnprocessableEntityError` | `TypeSafeUnprocessableEntityException` |
| 429 | `TypeSafeRateLimitError` (`retry_after_ms`) | `TypeSafeRateLimitException` (`RetryAfter`) |
| 5xx | `TypeSafeInternalServerError` | `TypeSafeInternalServerException` |
| — | `TypeSafeAPIConnectionError` | `TypeSafeApiConnectionException` |
| — | `TypeSafeAPITimeoutError` | `TypeSafeApiTimeoutException` (`Timeout`) |
| — | `TypeSafeAPIResponseValidationError` | `TypeSafeApiResponseValidationException` (`FieldPath`) |

Every HTTP error carries `StatusCode`, `Body`, `Headers`, `Endpoint`, and `RequestId` (read from `x-typesafe-request-id`).

```csharp
try
{
    var response = await client.SystemOneAsync(state, questions);
}
catch (TypeSafeRateLimitException ex)
{
    await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(1));
}
catch (TypeSafeAuthenticationException)
{
    // Key is wrong or missing — retrying will not help.
}
catch (TypeSafeApiTimeoutException ex)
{
    logger.LogWarning("Timed out after {Timeout}", ex.Timeout);
}
```

## Retry

`RetryPolicy` now matches the Python defaults exactly.

| Setting | Python | .NET |
| --- | --- | --- |
| Retries after the first attempt | 2 | `MaxRetries = 2` |
| First backoff | 0.5 s | `BackoffInitial` |
| Backoff ceiling | 5 s | `BackoffMax` |
| Jitter | 0.25 | `BackoffJitter` |
| Retried statuses | 408, 429, 500–599 | `HttpStatuses` |
| Honour `Retry-After` | yes | `RespectRetryAfter` |
| Retry connection errors | yes | `RetryConnectionErrors` |
| Retry timeouts | yes | `RetryTimeoutErrors` |
| Total budget per call | 30 s | `Budget` |

A policy can be set once on the client or overridden per call:

```csharp
var options = new TypeSafeOptions { Retry = new RetryPolicy(MaxRetries: 4) };
await client.SystemOneAsync(state, questions, retry: new RetryPolicy(0));   // no retry, just this once
```

Previously the .NET SDK retried only 429 and 529, and only when a policy was passed explicitly at the call site.

## Constants

`TypeSafeConstants` mirrors the Python `constants` module, so environment variable names never have to be retyped.

| Constant | Value |
| --- | --- |
| `ApiKeyEnv` | `TYPESAFE_API_KEY` |
| `BaseUrlEnv` | `TYPESAFE_BASE_URL` |
| `DefaultModelEnv` | `TYPESAFE_DEFAULT_MODEL` |
| `LogLevelEnv` | `TYPESAFE_LOG_LEVEL` |
| `DefaultBaseUrl` | `https://api.typesafe.ai` |
| `DefaultModel` | `jev-latest` |
| `DefaultTimeout` | 10 s |

`TypeSafeConstants.SecretHeaders` and `RedactHeader` exist so header values are never written to a log verbatim.

## Client options

| Python parameter | .NET |
| --- | --- |
| `api_key` | `TypeSafeOptions.ApiKey` |
| `base_url` | `TypeSafeOptions.Endpoint` |
| `model` | `TypeSafeOptions.DefaultModel` |
| `retry` | `TypeSafeOptions.Retry` |
| `timeout` | `TypeSafeOptions.Timeout` |
| `headers` | `TypeSafeOptions.Headers` |
| `http_client` / `transport` | `HttpClient` constructor argument |
| `extra_headers` (per call) | `extraHeaders` parameter |
| `extra_body` (per call) | `extraBody` parameter |

Every request carries `X-TypeSafe-SDK`, `X-TypeSafe-Runtime`, and — on a retried attempt — `X-TypeSafe-Retry-Count`.

## Deliberate differences

### Sync vs async
Python ships a synchronous client and an asynchronous one. .NET does not need the split: `SystemOneAsync` is the single async API, and callers who want to block can use `.GetAwaiter().GetResult()`. Adding a parallel synchronous surface would double the API for no benefit.

### `response_model`
Python's `system_one(response_model=…)` re-validates the response into a caller-supplied Pydantic model. The .NET equivalent is to deserialize `SystemOneResponse` yourself or map it to your own type; a generic overload would add reflection cost without adding safety that the type system does not already give.

### The simulator
The simulator has no counterpart in the Python SDK. It answers `choice`, `noul`, and `score` questions locally so tests, CI, and demos run with no key and no network. It is lexical, not semantic: it weights rare words in the criteria above common ones, honours negation (`no nesting` does not count as nesting), and returns a flat distribution with low confidence when it finds no signal rather than guessing. See [the SDK guide](sdk-guide.en.md) for when to trust it.

## Verifying parity

`TypeSafeSdk.Tests/ParityTests.cs` asserts each item on this page: status-code mapping, `request_id`, `retry-after`, timeout and connection errors, retried statuses, jitter bounds, identity headers, extra headers and body, constants, and the wire shape of every question type.

```bash
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~ParityTests"
```

Created by **Gravicode Studios**, led by Kang Fadhil.
