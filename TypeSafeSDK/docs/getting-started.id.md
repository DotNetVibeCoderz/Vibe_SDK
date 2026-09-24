# Getting Started — TypeSafe .NET SDK

**Bahasa Indonesia** · [English](getting-started.en.md)

Panduan cepat untuk mulai menggunakan TypeSafe SDK pada .NET 10.

## Prasyarat

- .NET SDK 10
- API key TypeSafe untuk mode live
- Windows, Linux, atau macOS

## Instalasi dari source

```bash
git clone <repository-url>
cd TypeSafeSDK
dotnet build TypeSafeSDK.slnx
dotnet test TypeSafeSDK.slnx
```

Tambahkan referensi project SDK:

```bash
dotnet add package Gravicode.TypeSafeSdk
```

> Paket NuGet akan dipublikasikan setelah seluruh aplikasi dan pengujian final selesai.

## Program pertama dengan simulator

Simulator tidak membutuhkan koneksi internet maupun API key.

```csharp
using TypeSafeSdk;

await using var client = new TypeSafeClient(
    new TypeSafeOptions { Simulator = true });

var response = await client.SystemOneAsync(
    new { document = "I was charged twice. Please fix this ASAP." },
    Choice.Create("billing", "technical", "other"));

var category = response.Choices?["category"].Choice;
Console.WriteLine(category); // billing
```

## Konfigurasi API key

API key tidak boleh ditulis di source code. Gunakan environment variable:

```powershell
$env:TYPESAFE_API_KEY = "your-api-key"
$env:TYPESAFE_BASE_URL = "https://api.typesafe.ai"
$env:TYPESAFE_DEFAULT_MODEL = "jev-latest"
```

Atau gunakan file lokal yang diparsing SDK:

```powershell
$env:TYPESAFE_API_KEY_FILE = "C:\secrets\TypeSafeApiKey.txt"
```

Format file yang didukung:

```text
typesafe api key: your-api-key
```

```json
{"api_key":"your-api-key"}
```

## Mode live

```bash
dotnet run --project TypeSafe.Cli -- classify "I was charged twice" --live
```

Endpoint resmi SDK Python yang digunakan SDK .NET:

```text
POST https://api.typesafe.ai/v1/systemone
```

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil.

## Melihatnya bekerja

`TypeSafe.JevGallery` adalah katalog Avalonia berisi lima belas use case yang bisa dijalankan, mencakup game, edukasi, pekerjaan, sains, dan simulasi. Tiap use case menampilkan jawaban, distribusi probabilitasnya, dan kode C# persis yang menghasilkannya.

```bash
dotnet run --project TypeSafe.JevGallery
```

## Selanjutnya

- [Panduan Penggunaan SDK](sdk-guide.id.md) — pertanyaan, error, retry, timeout, header
- [Kesejajaran dengan Python SDK](parity-python.id.md) — apa yang sejajar dan apa yang sengaja berbeda
- [Jev Gallery](jev-gallery.id.md) — katalog use case
