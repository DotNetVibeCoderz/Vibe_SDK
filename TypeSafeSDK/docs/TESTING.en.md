# Testing

[Bahasa Indonesia](TESTING.id.md) · **English**

## Running the suite

```bash
dotnet build TypeSafeSDK.slnx
dotnet test TypeSafeSDK.slnx
```

Last verified run:

```text
Passed: 96
Failed: 0
Skipped: 0
```

Run one group at a time with a filter:

```bash
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~ParityTests"
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~GalleryCatalogTests"
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~RetryPolicy_Retries429"
```

Nothing in the suite touches the network or needs an API key. HTTP behaviour is tested against `HttpMessageHandler` stubs, and everything else runs through the simulator.

## What is covered

### Wire contract (`ApiContractTests`, `OfficialContractTests`)
- `POST /v1/systemone` path, `Authorization` header, and the `state` / `model` / `questions` payload shape
- `GET /v1/models`
- `choice`, `noul`, and `score` question serialization
- Parsing all three answer types, `legend`, `probabilities`, and `usage`
- Unknown answer kinds and unknown response fields ignored, so a future API stays readable
- Status-code to exception mapping for 400, 401, 403, 404, 422, 429, and 529

### Python SDK parity (`ParityTests`)
- `request_id` from `x-typesafe-request-id`, and `retry-after-ms` / `retry-after`
- Typed timeout and connection failures
- Retry across 408, 429, and 5xx; `X-TypeSafe-Retry-Count` on a retried attempt
- Backoff caps, `Retry-After` precedence, and jitter staying inside its bounds
- `X-TypeSafe-SDK` and `X-TypeSafe-Runtime` identity headers, client headers, per-call `extraHeaders`, and `extraBody`
- Schema normalization — a raw `ChoiceSchema` is converted to the wire shape instead of being serialized as a record
- `TypeSafeConstants` values, secret-header redaction, and environment variable names
- `Usage` treating an absent count as null

### Python SDK 0.7.5 sync (`UpstreamSyncTests`)
- `box` question wire shape and `BoxAnswer` parsing, including null confidence and tolerated extra fields such as `assets_used`
- `noul` with a described outcome and no instructions
- Rejection before the network: null state, no questions, missing `type`, missing or empty criteria, and an undescribed `noul`
- `NaN` and `Infinity` sent as strings
- zstd compression on large bodies only, off with `RequestCompression.None`, never on `GET`, and the same compressed bytes on a retry
- Multipart layout for an image passed by alias, placed in state, and referenced while attached; overlapping `AttachAssets` scopes; one image under two names; `extraBody` assets replacing the SDK's; unreadable and empty images failing before the network; PNG/JPEG/unknown part labels
- The simulator answering `box` with the whole frame and confidence 0

### Simulator (`ParityTests`, `TypeSafeSdkTests`)
- Answers choice, noul, and score questions; probabilities sum to one; output is deterministic
- Words shared by every criterion do not decide the answer; a word unique to one criterion does
- Negated mentions are ignored — `no nesting observed` does not read as nesting
- State values are scored, field names are not
- A clearly negative input does not read true

### Jev Gallery catalogue (`GalleryCatalogTests`)
`Specimens.cs` is linked directly into the test project, so the catalogue is executed rather than merely described.

- Every one of the fifteen specimens runs and answers all of its questions
- Every choice answer is a label from that specimen's own criteria
- The demo answers for G-01, E-02, W-01, S-01, S-02, and M-03 stay correct and confident
- The nouls that should read true on their demo input still do

### API key loading (`ApiKeyLoaderTests`)
Raw, `KEY=VALUE`, `KEY: VALUE`, and JSON files; unknown formats rejected. The key is never printed.

### App Generator (`TypeSafeAppGen.Tests`)

77 offline tests for the non-UI parts of TypeSafeAppGen: the project path guard (including sibling-prefix escapes), the `dotnet build` diagnostic parser, the math expression evaluator, the code formatter, `app.config.json` migration and corrupt-file fallback, scaffolding of every template (no leftover placeholders or `.txt` files), a chat service for each of the five LLM providers, and the kernel functions against a fake host.

```bash
dotnet test TypeSafeAppGen.Tests/TypeSafeAppGen.Tests.csproj
```

The UI and live-model behaviour were checked by hand on 2026-09-25: Jack on Azure OpenAI `gpt-5-mini` edited a file and got the build passing, "Build the project and fix every error" repaired an injected CS1002, Settings › Test connection returned OK, and the Claude connector returned a mapped 401 for a fake key. All 12 templates were scaffolded and built with zero warnings.

## Live API verification

The suite is offline by design, so the live contract was checked by hand against `api.typesafe.ai` using the key file named by `TYPESAFE_API_KEY_FILE`.

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\secrets\TypeSafeApiKey.txt'
dotnet run --project TypeSafe.Cli -- models --live
dotnet run --project TypeSafe.Cli -- classify 'I was charged twice for my subscription and the refund never arrived' --live
dotnet run --project TypeSafe.Cli -- noul 'The deployment crashed and customers cannot check out' --question 'Is this urgent?' --live
dotnet run --project TypeSafe.Cli -- score 'Escort the caravan through the swamp at night while bandits track the road' --question 'How hard is this quest?' --levels 'trivial,steady,demanding,brutal' --live
```

Results, on 2026-09-24:

| Call | Result |
| --- | --- |
| `models` | `jev-latest` and `jev-preview`, each with a full ISO `release_date` |
| `classify` | `billing`, confidence 1, model `jev-1.13.0`, 311 in / 38 out tokens |
| `noul` | `0.97` |
| `score` | `2.18` with legend `trivial / steady / demanding / brutal` and probabilities `0 / 0.01 / 0.8 / 0.19` |
| `evaluate` (choice + noul + score in one call) | all three answered; `LegendByLevel` and `NearestLabel` populated |

Two things worth recording from that run. The live API reports `model` as the resolved build (`jev-1.13.0`) rather than the alias that was requested, and `release_date` is a full timestamp rather than `YYYY-MM-DD` — which is why `ModelMetadata.ReleaseDate` is a `string`. Both parse correctly.

On 2026-10-28, after the Python SDK 0.7.5 sync, a scratch script exercised the new paths live: a 2.4 KB body sent with `Content-Encoding: zstd` classified as `billing`; a `noul` with only `criteria.true` returned `0.84`; and a 400×300 PNG with a red circle at (240, 40, 120, 120) px and a blue square at (30, 180, 100, 100) px came back from `jev-preview` as boxes at (240, 39, 124, 120) and (32, 174, 100, 105), confidence 0.63 and 0.93 — `jev-latest` gave the same boxes. A `noul` referencing the attached image inside interpolated text read `0.99` for "contains a red shape". The resolved model was `jev-1.14.0`. Without `TypeSafe-Danger-Zone-Images: true` the API answers 400 with `api_usage_error`.

The Jev Gallery was also run against the live API end to end by setting `UseSimulator` to `false`. The same quest description that the simulator scores as `steady` comes back from the live model as `2.18 — demanding`, which is the honest measure of the gap between them.

## Package verification

The packed artifact is installed into a throwaway console app before release, to confirm it works outside this solution:

```bash
dotnet pack TypeSafeSdk/TypeSafeSdk.csproj -c Release -o artifacts
dotnet new console -o /tmp/pkgtest
dotnet add /tmp/pkgtest package Gravicode.TypeSafeSdk --source ./artifacts --version 1.2.0
```

A plain `Microsoft.NET.Sdk` console app must compile and run — the SDK deliberately depends on `Microsoft.Extensions.Logging.Abstractions` rather than the ASP.NET Core shared framework so that it can.

Created by **Gravicode Studios**, led by Kang Fadhil.
