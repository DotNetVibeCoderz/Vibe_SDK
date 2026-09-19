# TypeSafe .NET SDK

SDK .NET 10 strongly typed untuk workflow klasifikasi TypeSafe, simulator lokal, CLI, REST API, Blazor Server, dan .NET Notebook.

## Quick start
```bash
dotnet build TypeSafeSDK.slnx
dotnet test TypeSafeSDK.slnx
dotnet run --project TypeSafe.Cli -- classify "I was charged twice"
```

## Dokumentasi
- [Getting Started Indonesia](docs/getting-started.id.md)
- [Getting Started English](docs/getting-started.en.md)
- [Panduan SDK Indonesia](docs/sdk-guide.id.md)
- [SDK Guide English](docs/sdk-guide.en.md)
- [Notebook examples](notebooks/README.md)
- [Testing](docs/TESTING.md)

Endpoint resmi yang diikuti:

```text
POST https://api.typesafe.ai/v1/systemone
```

Live API menggunakan secret lokal, tidak pernah commit ke repo:
```powershell
$env:TYPESAFE_API_KEY_FILE = Get-Content 'C:\Users\mifma\Documents\CodeSandbox\TypeSafeApiKey.txt' -Raw
dotnet run --project TypeSafe.Cli -- classify "I was charged twice" --live
```

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil. AI assistant: **Jack — The Code Bender**.
