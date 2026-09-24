# TypeSafe .NET SDK

**English** · [Bahasa Indonesia](README.md)

A strongly typed .NET 10 SDK for TypeSafe classification workflows, with a local simulator, a CLI, a REST API, Blazor Server, desktop apps, and .NET Notebooks.

An unofficial port of the [official Python SDK](https://github.com/typesafe-ai/typesafe-sdk-python).

```bash
dotnet add package Gravicode.TypeSafeSdk
```

## Quick start

```bash
dotnet build TypeSafeSDK.slnx
dotnet test TypeSafeSDK.slnx
dotnet run --project TypeSafe.Cli -- classify "I was charged twice"
dotnet run --project TypeSafe.JevGallery
```

```csharp
using TypeSafeSdk;

await using var client = new TypeSafeClient(TypeSafeOptions.FromEnvironment());

var response = await client.SystemOneAsync(
    new { document = ticket.Body },
    new Dictionary<string, object>
    {
        ["category"] = Choice.Create("billing", "technical", "other"),
        ["urgent"]   = new NoulSchema("Does this need a response within one hour?"),
        ["tone"]     = new ScoreSchema("How frustrated is the customer?", "calm", "annoyed", "angry")
    });

ticket.Queue = response.Choices["category"].Choice;
ticket.Sla   = response.Nouls["urgent"].IsTrue ? Sla.OneHour : Sla.NextDay;
ticket.Tone  = response.Scores["tone"].NearestLabel;
```

Every answer carries its full probability distribution, so an ambiguous case is visible rather than hidden behind a single label.

## Jev Gallery

An Avalonia catalogue of fifteen runnable use cases — games, education, work, science, and simulation — each with sample code and the probability distribution behind its answer.

![Jev Gallery](docs/images/jev-gallery.png)

```bash
dotnet run --project TypeSafe.JevGallery
```

## Documentation

| English | Bahasa Indonesia |
| --- | --- |
| [Getting Started](docs/getting-started.en.md) | [Panduan Awal](docs/getting-started.id.md) |
| [SDK Guide](docs/sdk-guide.en.md) | [Panduan SDK](docs/sdk-guide.id.md) |
| [Parity with the Python SDK](docs/parity-python.en.md) | [Kesejajaran dengan Python SDK](docs/parity-python.id.md) |
| [Jev Gallery](docs/jev-gallery.en.md) | [Jev Gallery](docs/jev-gallery.id.md) |
| [Publishing to NuGet](docs/nuget.en.md) | [Publikasi NuGet](docs/nuget.id.md) |
| [Board Games](docs/board-games.en.md) | [Board Games](docs/board-games.id.md) |
| [Testing](docs/TESTING.en.md) | [Pengujian](docs/TESTING.id.md) |
| [Notebook examples](notebooks/README.en.md) | [Contoh notebook](notebooks/README.md) |

## Applications

| Project | What it is |
| --- | --- |
| `TypeSafe.Cli` | `classify`, `noul`, `score`, `evaluate`, `models`, `validate`, `config` |
| `TypeSafe.JevGallery` | Avalonia catalogue of fifteen use cases |
| `TypeSafe.Api` | Minimal API wrapper |
| `TypeSafe.Blazor` | Blazor Server ticket classifier |
| `TypeSafe.BoardGames` | WPF Tic Tac Toe, Othello, Connect Four |
| `TypeSafeAppGen` | Avalonia code editor for the Jack assistant |

## Endpoint

```text
POST https://api.typesafe.ai/v1/systemone
GET  https://api.typesafe.ai/v1/models
```

## Live API

Keys are never committed. The SDK reads one from the environment or from a local file:

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\secrets\TypeSafeApiKey.txt'
dotnet run --project TypeSafe.Cli -- classify "I was charged twice" --live
```

## Without an API key

`Simulator = true` answers `choice`, `noul`, and `score` questions locally and deterministically, so unit tests and CI run with no network.

```csharp
await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
```

## Licence

MIT. See [LICENSE](LICENSE).

Created by **Gravicode Studios**, led by Kang Fadhil. AI assistant: **Jack — The Code Bender**.
