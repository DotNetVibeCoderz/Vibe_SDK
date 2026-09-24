# TypeSafe .NET Documentation

[Bahasa Indonesia](README.id.md) · **English**

- [Getting Started](getting-started.en.md)
- [SDK Usage Guide](sdk-guide.en.md)
- [Parity with the Python SDK](parity-python.en.md)
- [Jev Gallery](jev-gallery.en.md)
- [Publishing to NuGet](nuget.en.md)
- [Testing](TESTING.en.md)
- [.NET Notebook Examples](../notebooks/README.en.md)
- [TypeSafe Board Games](board-games.en.md)

## Install

```bash
dotnet add package Gravicode.TypeSafeSdk
```

The package id is prefixed because this is an unofficial port. The namespace stays `TypeSafeSdk`.

## Endpoint

The SDK follows the official Python SDK contract: `POST https://api.typesafe.ai/v1/systemone` with `state`, `model`, and `questions`, plus `GET /v1/models`.

## Configuration

`TypeSafeOptions.FromEnvironment()` reads `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL`, `TYPESAFE_LOG_LEVEL`, and `TYPESAFE_SIMULATOR`. The names are constants on `TypeSafeConstants`. Secrets are never stored in source control; `TypeSafeApiKeyLoader` reads a key file named by `TYPESAFE_API_KEY_FILE`.

## Simulator

`Simulator = true` answers `choice`, `noul`, and `score` questions locally with no key and no network. It is lexical rather than semantic: it weights rare criteria words above common ones, honours negation, and abstains with a flat distribution when it finds no signal. See [the SDK guide](sdk-guide.en.md#14-the-simulator) for when to trust it.

## Applications in this repository

| Project | What it is |
| --- | --- |
| `TypeSafe.Cli` | `classify`, `noul`, `score`, `evaluate`, `models`, `validate`, `config` |
| `TypeSafe.JevGallery` | Avalonia catalogue of fifteen runnable use cases |
| `TypeSafe.Api` | Minimal API wrapper |
| `TypeSafe.Blazor` | Blazor Server ticket classifier |
| `TypeSafe.BoardGames` | WPF Tic Tac Toe, Othello, Connect Four |
| `TypeSafeAppGen` | Avalonia code editor for the Jack assistant |

```bash
dotnet run --project TypeSafe.Cli -- classify "I was charged twice"
dotnet run --project TypeSafe.JevGallery
```

Created by **Gravicode Studios**, led by Kang Fadhil.
