# TypeSafe .NET SDK

**Bahasa Indonesia** · [English](README.en.md)

SDK .NET 10 strongly typed untuk workflow klasifikasi TypeSafe, ditambah simulator lokal, CLI, REST API, Blazor Server, aplikasi desktop, dan .NET Notebook.

Port tidak resmi dari [Python SDK resmi](https://github.com/typesafe-ai/typesafe-sdk-python).

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

Setiap jawaban membawa distribusi probabilitas penuh, sehingga kasus ambigu terlihat, bukan tersembunyi di balik satu label.

## Jev Gallery

Katalog Avalonia berisi lima belas use case yang bisa dijalankan — game, edukasi, pekerjaan, sains, dan simulasi — lengkap dengan kode contoh dan distribusi probabilitas tiap jawaban.

![Jev Gallery](docs/images/jev-gallery.png)

```bash
dotnet run --project TypeSafe.JevGallery
```

## TypeSafe App Generator

Code editor Avalonia dengan **Jack — The Code Bender**. Jelaskan aplikasi yang Anda mau; Jack menulis file-nya, menjalankan build, dan memperbaiki error sampai berhasil. Mendukung OpenAI, Azure OpenAI, Claude, Gemini, dan Ollama lewat Semantic Kernel, plus 12 template siap jalan (3D, animasi, game, simulator, web, AI).

![TypeSafe App Generator](docs/images/appgen-jack-edit.png)

```bash
dotnet run --project TypeSafeAppGen
```

## Dokumentasi

| Indonesia | English |
| --- | --- |
| [Panduan Awal](docs/getting-started.id.md) | [Getting Started](docs/getting-started.en.md) |
| [Panduan SDK](docs/sdk-guide.id.md) | [SDK Guide](docs/sdk-guide.en.md) |
| [Kesejajaran dengan Python SDK](docs/parity-python.id.md) | [Parity with the Python SDK](docs/parity-python.en.md) |
| [Jev Gallery](docs/jev-gallery.id.md) | [Jev Gallery](docs/jev-gallery.en.md) |
| [TypeSafe App Generator](docs/app-generator.id.md) | [TypeSafe App Generator](docs/app-generator.en.md) |
| [Publikasi NuGet](docs/nuget.id.md) | [Publishing to NuGet](docs/nuget.en.md) |
| [Board Games](docs/board-games.id.md) | [Board Games](docs/board-games.en.md) |
| [Pengujian](docs/TESTING.id.md) | [Testing](docs/TESTING.en.md) |
| [Contoh notebook](notebooks/README.md) | [Notebook examples](notebooks/README.en.md) |

## Aplikasi

| Proyek | Isinya |
| --- | --- |
| `TypeSafe.Cli` | `classify`, `noul`, `score`, `evaluate`, `models`, `validate`, `config` |
| `TypeSafe.JevGallery` | Katalog Avalonia lima belas use case |
| `TypeSafe.Api` | Pembungkus Minimal API |
| `TypeSafe.Blazor` | Pengklasifikasi tiket Blazor Server |
| `TypeSafe.BoardGames` | WPF Tic Tac Toe, Othello, Connect Four |
| `TypeSafeAppGen` | Code editor Avalonia dengan Jack: explorer, editor bertab, build/run/deploy, 12 template, 5 penyedia LLM |

## Endpoint

```text
POST https://api.typesafe.ai/v1/systemone
GET  https://api.typesafe.ai/v1/models
```

## Live API

Key tidak pernah di-commit. SDK membacanya dari environment atau dari file lokal:

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\secrets\TypeSafeApiKey.txt'
dotnet run --project TypeSafe.Cli -- classify "I was charged twice" --live
```

## Tanpa API key

`Simulator = true` menjawab pertanyaan `choice`, `noul`, dan `score` secara lokal dan deterministik, sehingga unit test dan CI berjalan tanpa jaringan.

```csharp
await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
```

## Lisensi

MIT. Lihat [LICENSE](LICENSE).

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil. AI assistant: **Jack — The Code Bender**.
