# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

All projects target **.NET 10**. The solution uses the new XML `.slnx` format.

```bash
dotnet build TypeSafeSDK.slnx
dotnet test TypeSafeSDK.slnx

# single test / subset (xUnit)
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~RetryPolicy_Retries429"
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~ParityTests"
dotnet test TypeSafeAppGen.Tests/TypeSafeAppGen.Tests.csproj   # AppGen logic, offline

# run the apps
dotnet run --project TypeSafe.Cli -- classify "I was charged twice"   # simulator (default)
dotnet run --project TypeSafe.JevGallery                              # Avalonia use-case catalogue
dotnet run --project TypeSafe.Api                                     # minimal API
dotnet run --project TypeSafe.Blazor                                  # Blazor Server
dotnet run --project TypeSafeAppGen                                   # Avalonia App Generator
dotnet run --project TypeSafe.BoardGames                              # WPF (Windows only, net10.0-windows)

# package
dotnet pack TypeSafeSdk/TypeSafeSdk.csproj -c Release -o artifacts
```

Live API calls need a key file; the path is read from `TYPESAFE_API_KEY_FILE` (the CLI falls back to a hardcoded local path). Keys live outside the repo and are never printed or committed.

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\Users\mifma\Documents\CodeSandbox\TypeSafeApiKey.txt'
dotnet run --project TypeSafe.Cli -- classify 'I was charged twice' --live
```

## Architecture

This is an unofficial .NET port of the TypeSafe Python SDK, plus sample apps that exercise it. Everything funnels through one endpoint: `POST {Endpoint}/v1/systemone` (plus `GET /v1/models`).

**`TypeSafeSdk/` — the core library.** Published to NuGet as `Gravicode.TypeSafeSdk`; the assembly and namespace stay `TypeSafeSdk`.

- `TypeSafeClient.cs` — `SystemOneAsync` overloads, `TypeSafeOptions`, and one internal `SendAsync` that every request (including `ModelsClient`) goes through. That method owns retry, timeout, identity headers, and error mapping — put cross-cutting request behaviour there, not in callers. When `Options.Simulator` is true the client short-circuits and never touches the network.
- `Models.cs` — question schemas, answer parsing, and `TypeSafeSimulator`. Schemas implement `IQuestionSchema`; `QuestionPayload.Normalize` converts any accepted `questions` shape into the wire format, so **never serialize a schema record directly** — that was a real bug. `SystemOneResponse.Parse` hand-parses `JsonElement`, silently skips unknown answer types for forward compatibility, and accepts a legacy `choices` shape.
- `Errors.cs` — the typed exception hierarchy and `TypeSafeErrorFactory.Create`, which maps status codes. `TypeSafeApiException` is the base for every HTTP failure, so existing `catch` blocks still work — but `Assert.Throws<T>` in tests needs the exact derived type or `ThrowsAny`.
- `RetryPolicy.cs` — a record matching the Python defaults (408/429/5xx, jitter, `Retry-After`, 30s budget).
- `Constants.cs` — `TypeSafeConstants`: env var names, defaults, header names, `SdkVersion`, and secret-header redaction.

`TypeSafeConstants.SdkVersion` and `<Version>` in the csproj must be bumped together — the version is sent in the `X-TypeSafe-SDK` header.

**The simulator is lexical, not semantic.** It weights criteria words by inverse document frequency (a word in every criterion decides nothing), honours negation, scores state *values* rather than field names, and returns a flat distribution with low confidence when it finds no signal instead of guessing. Enriching a `Choice`'s criteria descriptions is the usual fix when a demo answers weakly — that helps the live model too.

**Consumers.** Every sample references `TypeSafeSdk` by project reference and defaults to simulator mode so builds, tests, and demos stay CI-safe:

- `TypeSafe.Cli` — `classify | noul | score | evaluate | models | validate | config`; `--live` loads the key file.
- `TypeSafe.JevGallery` — Avalonia catalogue of 15 use cases. `Specimens.cs` holds the data and has no Avalonia dependency, which is why the test project links it directly and executes every specimen. UI is built in code-behind, not bindings, matching the other desktop apps. Design tokens live in `App.axaml`.
- `TypeSafe.Api` / `TypeSafe.Blazor` / `TypeSafe.BoardGames` — minimal API, Blazor Server, and WPF board games (the SDK only ever sees legal moves; a local tactical fallback guarantees a valid move).
- `TypeSafeAppGen` — Avalonia code editor for "Jack". `Ai/JackAgent.cs` builds a fresh `Kernel` per message from `Ai/LlmFactory.cs` (OpenAI, Azure OpenAI, Claude via the official `Anthropic` SDK's `IChatClient`, Gemini, Ollama) and streams with auto function calling. Kernel functions live in `Ai/Plugins/`; every file access goes through `ProjectWorkspace.Resolve`, which confines paths to the open project — keep it that way. Plugins talk to the UI only through `IJackHost`, whose implementation in `MainWindow.Jack.cs` marshals to the UI thread because functions run on the thread pool. `MainWindow` is split into partials (Project, Build, Jack, Start) and built in code-behind. Project templates are in `Templates/<id>/` with a `.txt` suffix and a `template.json`; `__ProjectName__` is replaced on scaffold, and `_base/` holds shared skeletons. Settings persist to `app.config.json` next to the exe (the checked-in copy must never contain keys). See `docs/app-generator.*.md`.

**Tests**: `TypeSafeAppGen.Tests/` covers AppGen's non-UI logic (path guard, diagnostic parser, formatter, config migration, template scaffolding, kernel functions against a fake `IJackHost`). `TypeSafeSdk.Tests/` holds contract tests, not unit tests of internals: `HttpMessageHandler` stubs assert the exact URI, headers, and JSON payload against the Python SDK, plus status mapping, retry, timeout, and simulator behaviour. `GalleryCatalogTests` runs all 15 gallery specimens end to end. The whole suite is offline — live API checks are recorded in `docs/TESTING.en.md` rather than automated.

## Conventions

- **Code style is deliberately dense in the older files**: the CLI, board games, and some `.csproj` files pack whole classes onto very long single lines. Match the density of the file you are editing; newer files (`Errors.cs`, `Specimens.cs`, the gallery) are conventionally formatted. Do not reformat a dense file wholesale — it produces a huge, noisy diff.
- Records and `sealed` by default; nullable enabled.
- Docs live in `docs/` and are **bilingual** — every guide has an `.id.md` and `.en.md` pair; XML doc comments in the SDK are in Indonesian.
- `PLAN.md` is the roadmap, `Progress.md` is the dated development log. Update both when completing meaningful work.
- Apps and docs carry the attribution "Gravicode Studios, dipimpin Kang Fadhil"; the AI assistant persona is "Jack — The Code Bender".
- New UI work is expected to use the `frontend-design` skill vendored at `.claude/skills/frontend-design/`.
- `requirements.md` is the original product spec and remains the source of truth for intended features not yet built.
