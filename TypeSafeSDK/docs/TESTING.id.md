# Pengujian

**Bahasa Indonesia** · [English](TESTING.en.md)

## Menjalankan seluruh test

```bash
dotnet build TypeSafeSDK.slnx
dotnet test TypeSafeSDK.slnx
```

Hasil verifikasi terakhir:

```text
Passed: 96
Failed: 0
Skipped: 0
```

Menjalankan satu kelompok saja dengan filter:

```bash
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~ParityTests"
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~GalleryCatalogTests"
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~RetryPolicy_Retries429"
```

Tidak ada satu pun test yang menyentuh jaringan atau memerlukan API key. Perilaku HTTP diuji terhadap stub `HttpMessageHandler`, sisanya berjalan lewat simulator.

## Cakupan

### Kontrak wire (`ApiContractTests`, `OfficialContractTests`)
- Path `POST /v1/systemone`, header `Authorization`, dan bentuk payload `state` / `model` / `questions`
- `GET /v1/models`
- Serialisasi pertanyaan `choice`, `noul`, dan `score`
- Parsing ketiga tipe answer, `legend`, `probabilities`, dan `usage`
- Tipe answer tak dikenal dan field response tak dikenal diabaikan, agar API masa depan tetap terbaca
- Pemetaan status ke exception untuk 400, 401, 403, 404, 422, 429, dan 529

### Kesejajaran dengan Python SDK (`ParityTests`)
- `request_id` dari `x-typesafe-request-id`, serta `retry-after-ms` / `retry-after`
- Kegagalan timeout dan koneksi yang bertipe
- Retry pada 408, 429, dan 5xx; header `X-TypeSafe-Retry-Count` pada percobaan ulang
- Batas atas backoff, prioritas `Retry-After`, dan jitter yang tetap di dalam rentangnya
- Header identitas `X-TypeSafe-SDK` dan `X-TypeSafe-Runtime`, header client, `extraHeaders` per panggilan, dan `extraBody`
- Normalisasi schema — `ChoiceSchema` mentah diubah ke bentuk wire, bukan diserialisasi sebagai record
- Nilai `TypeSafeConstants`, penyensoran header rahasia, dan nama environment variable
- `Usage` memperlakukan angka yang tidak dilaporkan sebagai null

### Simulator (`ParityTests`, `TypeSafeSdkTests`)
- Menjawab pertanyaan choice, noul, dan score; probabilitas berjumlah satu; keluarannya deterministik
- Kata yang dimiliki semua kriteria tidak menentukan jawaban; kata yang unik pada satu kriteria menentukan
- Penyebutan yang dinegasikan diabaikan — `no nesting observed` tidak terbaca sebagai nesting
- Yang dinilai adalah nilai pada state, bukan nama field
- Input yang jelas-jelas negatif tidak terbaca true

### Katalog Jev Gallery (`GalleryCatalogTests`)
`Specimens.cs` ditautkan langsung ke project test, sehingga katalognya dieksekusi, bukan sekadar dideskripsikan.

- Kelima belas specimen berjalan dan menjawab seluruh pertanyaannya
- Setiap jawaban choice adalah label dari kriteria specimen itu sendiri
- Jawaban demo untuk G-01, E-02, W-01, S-01, S-02, dan M-03 tetap benar dan berconfidence memadai
- Noul yang seharusnya terbaca true pada input demo-nya tetap demikian

### Pemuatan API key (`ApiKeyLoaderTests`)
File format mentah, `KEY=VALUE`, `KEY: VALUE`, dan JSON; format tak dikenal ditolak. Key tidak pernah dicetak.

### App Generator (`TypeSafeAppGen.Tests`)

77 test offline untuk bagian non-UI TypeSafeAppGen: path guard proyek (termasuk lolos lewat folder bertetangga dengan prefiks sama), parser diagnostic `dotnet build`, evaluator ekspresi matematika, code formatter, migrasi `app.config.json` dan fallback saat file rusak, scaffolding setiap template (tanpa sisa placeholder atau file `.txt`), chat service untuk kelima penyedia LLM, dan kernel functions dengan host palsu.

```bash
dotnet test TypeSafeAppGen.Tests/TypeSafeAppGen.Tests.csproj
```

UI dan perilaku dengan model live diperiksa manual pada 2026-09-25: Jack di Azure OpenAI `gpt-5-mini` mengedit file dan membuat build berhasil, "Build the project and fix every error" memperbaiki CS1002 yang disisipkan, Settings › Test connection mengembalikan OK, dan konektor Claude mengembalikan 401 yang dipetakan untuk key palsu. Ke-12 template di-scaffold dan di-build tanpa warning.

## Verifikasi API live

Seluruh test sengaja offline, jadi kontrak live diperiksa manual terhadap `api.typesafe.ai` memakai file key yang ditunjuk `TYPESAFE_API_KEY_FILE`.

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\secrets\TypeSafeApiKey.txt'
dotnet run --project TypeSafe.Cli -- models --live
dotnet run --project TypeSafe.Cli -- classify 'I was charged twice for my subscription and the refund never arrived' --live
dotnet run --project TypeSafe.Cli -- noul 'The deployment crashed and customers cannot check out' --question 'Is this urgent?' --live
dotnet run --project TypeSafe.Cli -- score 'Escort the caravan through the swamp at night while bandits track the road' --question 'How hard is this quest?' --levels 'trivial,steady,demanding,brutal' --live
```

Hasil, 24 September 2026:

| Panggilan | Hasil |
| --- | --- |
| `models` | `jev-latest` dan `jev-preview`, masing-masing dengan `release_date` ISO lengkap |
| `classify` | `billing`, confidence 1, model `jev-1.13.0`, token 311 masuk / 38 keluar |
| `noul` | `0.97` |
| `score` | `2.18` dengan legend `trivial / steady / demanding / brutal` dan probabilitas `0 / 0,01 / 0,8 / 0,19` |
| `evaluate` (choice + noul + score dalam satu panggilan) | ketiganya terjawab; `LegendByLevel` dan `NearestLabel` terisi |

Dua hal layak dicatat dari sesi itu. API live melaporkan `model` sebagai build yang terselesaikan (`jev-1.13.0`), bukan alias yang diminta, dan `release_date` berupa timestamp penuh, bukan `YYYY-MM-DD` — itulah sebabnya `ModelMetadata.ReleaseDate` bertipe `string`. Keduanya terparsing benar.

Jev Gallery juga dijalankan penuh terhadap API live dengan mengubah `UseSimulator` menjadi `false`. Deskripsi quest yang sama yang dinilai `steady` oleh simulator dikembalikan model live sebagai `2.18 — demanding`, dan itulah ukuran jujur jarak di antara keduanya.

## Verifikasi paket

Artefak hasil pack dipasang ke console app sekali pakai sebelum rilis, untuk memastikan ia bekerja di luar solution ini:

```bash
dotnet pack TypeSafeSdk/TypeSafeSdk.csproj -c Release -o artifacts
dotnet new console -o /tmp/pkgtest
dotnet add /tmp/pkgtest package Gravicode.TypeSafeSdk --source ./artifacts --version 1.1.0
```

Console app `Microsoft.NET.Sdk` biasa harus bisa dikompilasi dan dijalankan — SDK ini sengaja bergantung pada `Microsoft.Extensions.Logging.Abstractions`, bukan shared framework ASP.NET Core, supaya hal itu mungkin.

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil.
