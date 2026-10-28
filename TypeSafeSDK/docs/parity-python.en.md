# Parity with the official Python SDK

[Bahasa Indonesia](parity-python.id.md) · **English**

This .NET SDK is an unofficial port of [`typesafe-ai/typesafe-sdk-python`](https://github.com/typesafe-ai/typesafe-sdk-python). This page records what was compared, what is now at parity, and what deliberately differs because .NET is not Python.

Last synced against Python SDK **v0.7.5** (2026-10-14), in .NET SDK 1.2.0.

## Public surface, side by side

| Python SDK | .NET SDK | Status |
| --- | --- | --- |
| `TypeSafeClient` / `AsyncTypeSafeClient` | `TypeSafeClient` (async) | Adapted — see [Sync vs async](#sync-vs-async) |
| `client.system_one(state, questions, …)` | `client.SystemOneAsync(state, questions, …)` | Parity |
| `client.models.list()` | `client.Models.ListAsync()` | Parity |
| `Choice`, `Score`, `Noul`, `Box` | `ChoiceSchema`, `ScoreSchema`, `NoulSchema`, `BoxSchema` | Parity |
| `NoulCriteria` (`true` / `false`), instructions optional when an outcome is described | `NoulSchema(instructions?, True, False)` | Parity |
| `Image`, `Asset` | `ImageAsset`, `Asset` | Adapted — see [Images](#images) |
| `system_one(assets=…)` | `SystemOneAsync(…, assets: …)` | Parity |
| `client.attach_assets(…)` (context manager) | `client.AttachAssets(…)` (`IDisposable`) | Parity |
| `compression="zstd" \| False` | `TypeSafeOptions.Compression` (`Zstd` / `None`) | Parity |
| `ChoiceModel.criteria` mapping label → description | `Choice.Create(IReadOnlyDictionary<string, string?>)` | Parity |
| `Question`, `Questions`, `QuestionModel` type aliases | `IQuestionSchema`, `QuestionsSchema`, `QuestionPayload.Normalize` | Adapted |
| `SystemOneResponse` with `.nouls` / `.choices` / `.scores` / `.boxes` | Same, plus `.ChoiceAnswers` and `.RequestId` | Parity + |
| `Answer`, `NoulAnswer`, `ChoiceAnswer`, `ScoreAnswer`, `BoxAnswer` | Same names, `Answer` is a polymorphic record | Parity |
| `BoxCoordinates` | `BoxCoordinates`, plus `Width`, `Height`, `ToPixels` | Parity + |
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

## Requests

These behaviours arrived in Python SDK 0.7.3–0.7.5 and are matched here:

- **Validation before the network.** A null state, no questions, a question without a string `type`, a `choice` or `score` without criteria, an empty criteria list or map, and a `noul` with neither instructions nor a described outcome all throw `TypeSafeException` without sending anything. Unknown question types and extra fields still pass through untouched, so a feature the API adds before this SDK models it remains usable.
- **Non-finite numbers.** JSON has no `NaN` or `Infinity` token; they are sent as the strings `"NaN"`, `"Infinity"`, and `"-Infinity"` instead of failing to serialize.
- **Compression.** Bodies of at least 1024 bytes are sent with `Content-Encoding: zstd`, compressed once and reused across retries. `GET /v1/models` has no body and is never compressed.
- **Multipart.** A request with images is `multipart/form-data`: a `request` part holding the JSON body, then one part per image, named `asset-0`, `asset-1`, …, which the body's `assets` map names. An image sent under two names is sent twice, because the API requires each part to be claimed by exactly one asset. An `assets` key in `extraBody` replaces the SDK's own, and then no image is read.

## Images

Python calls the class `Image`. In .NET that name collides with `Avalonia.Controls.Image`, `System.Drawing.Image`, and WPF's `Image` in the very apps that use this SDK, so it is `ImageAsset` here; `Asset` keeps its name. The factories mirror Python's constructors: `FromBytes` (raw bytes and buffers), `FromFile` (path-like), `FromBase64`, and `FromStream`. There is no `from_pil`; encode a `System.Drawing`/`SkiaSharp`/`ImageSharp` image to PNG or JPEG bytes and use `FromBytes`.

In Python, `instructions` and `criteria` accept an `Image` directly, because they are typed as `Content`. The .NET schemas keep `string` for those fields, so a typed schema references an image through its alias or reference inside the text — `new BoxSchema($"The dog next to {photo}")` with the asset attached or passed. A raw question dictionary may hold an `ImageAsset` anywhere, exactly as in Python.

While images are experimental, the API answers `400` unless the request carries `TypeSafe-Danger-Zone-Images: true`. Like the Python SDK, this SDK does not send it on its own; the name is available as `TypeSafeConstants.ImagesOptInHeader`.

## Deliberate differences

### Sync vs async
Python ships a synchronous client and an asynchronous one. .NET does not need the split: `SystemOneAsync` is the single async API, and callers who want to block can use `.GetAwaiter().GetResult()`. Adding a parallel synchronous surface would double the API for no benefit.

### `response_model`
Python's `system_one(response_model=…)` re-validates the response into a caller-supplied Pydantic model. The .NET equivalent is to deserialize `SystemOneResponse` yourself or map it to your own type; a generic overload would add reflection cost without adding safety that the type system does not already give.

### Transport details
The Python SDK pins connection-pool keep-alive to 30 s, works around a `truststore` TLS bug, offers an `http2` extra, and truncates logged bodies. None of these apply to `HttpClient`: `SocketsHttpHandler` already pools connections, uses the OS certificate store, and supports HTTP/2 through `HttpClient.DefaultRequestVersion`; this SDK never logs request bodies.

### The simulator
The simulator has no counterpart in the Python SDK. It answers `choice`, `noul`, and `score` questions locally so tests, CI, and demos run with no key and no network. It is lexical, not semantic: it weights rare words in the criteria above common ones, honours negation (`no nesting` does not count as nesting), and returns a flat distribution with low confidence when it finds no signal rather than guessing. It cannot see images, so a `box` question gets the whole frame with confidence 0. See [the SDK guide](sdk-guide.en.md) for when to trust it.

## Verifying parity

`TypeSafeSdk.Tests/ParityTests.cs` and `UpstreamSyncTests.cs` assert each item on this page: status-code mapping, `request_id`, `retry-after`, timeout and connection errors, retried statuses, jitter bounds, identity headers, extra headers and body, constants, and the wire shape of every question type, box answers, validation before the network, non-finite numbers, compression, and the multipart layout for every way of sending an image.

```bash
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~ParityTests|FullyQualifiedName~UpstreamSyncTests"
```

Created by **Gravicode Studios**, led by Kang Fadhil.
