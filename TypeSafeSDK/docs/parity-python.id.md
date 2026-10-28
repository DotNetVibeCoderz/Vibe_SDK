# Kesejajaran dengan Python SDK resmi

**Bahasa Indonesia** · [English](parity-python.en.md)

SDK .NET ini adalah port tidak resmi dari [`typesafe-ai/typesafe-sdk-python`](https://github.com/typesafe-ai/typesafe-sdk-python). Halaman ini mencatat apa yang dibandingkan, apa yang kini sejajar, dan apa yang sengaja berbeda karena .NET bukan Python.

Terakhir disejajarkan dengan Python SDK **v0.7.5** (2026-10-14), pada .NET SDK 1.2.0.

## Permukaan publik, berdampingan

| Python SDK | SDK .NET | Status |
| --- | --- | --- |
| `TypeSafeClient` / `AsyncTypeSafeClient` | `TypeSafeClient` (async) | Disesuaikan — lihat [Sync vs async](#sync-vs-async) |
| `client.system_one(state, questions, …)` | `client.SystemOneAsync(state, questions, …)` | Sejajar |
| `client.models.list()` | `client.Models.ListAsync()` | Sejajar |
| `Choice`, `Score`, `Noul`, `Box` | `ChoiceSchema`, `ScoreSchema`, `NoulSchema`, `BoxSchema` | Sejajar |
| `NoulCriteria` (`true` / `false`), instruksi opsional bila salah satu hasil dideskripsikan | `NoulSchema(instructions?, True, False)` | Sejajar |
| `Image`, `Asset` | `ImageAsset`, `Asset` | Disesuaikan — lihat [Gambar](#gambar) |
| `system_one(assets=…)` | `SystemOneAsync(…, assets: …)` | Sejajar |
| `client.attach_assets(…)` (context manager) | `client.AttachAssets(…)` (`IDisposable`) | Sejajar |
| `compression="zstd" \| False` | `TypeSafeOptions.Compression` (`Zstd` / `None`) | Sejajar |
| `ChoiceModel.criteria` label → deskripsi | `Choice.Create(IReadOnlyDictionary<string, string?>)` | Sejajar |
| Alias tipe `Question`, `Questions`, `QuestionModel` | `IQuestionSchema`, `QuestionsSchema`, `QuestionPayload.Normalize` | Disesuaikan |
| `SystemOneResponse` dengan `.nouls` / `.choices` / `.scores` / `.boxes` | Sama, plus `.ChoiceAnswers` dan `.RequestId` | Sejajar + |
| `Answer`, `NoulAnswer`, `ChoiceAnswer`, `ScoreAnswer`, `BoxAnswer` | Nama sama, `Answer` berupa record polimorfik | Sejajar |
| `BoxCoordinates` | `BoxCoordinates`, plus `Width`, `Height`, `ToPixels` | Sejajar + |
| `ScoreAnswer.legend: dict[int, str]` | `Legend` (kunci string) plus `LegendByLevel` dan `NearestLabel` | Sejajar + |
| `Usage.input_tokens / output_tokens` nullable | `Usage.InputTokens / OutputTokens` (`int?`) plus `TotalTokens` | Sejajar + |
| `ListModelsResponse.models` | `ListModelsResponse`, bisa langsung dipakai sebagai list | Sejajar |
| `ModelMetadata.name / description / release_date` | Sama, `release_date` dipetakan eksplisit | Sejajar |
| `RetryPolicy` | record `RetryPolicy` | Sejajar — lihat [Retry](#retry) |
| Modul `constants` | `TypeSafeConstants` | Sejajar |
| 12 error bertipe | 12 exception bertipe | Sejajar — lihat [Error](#error) |

## Error

Python SDK memetakan status HTTP ke kelas error spesifik. SDK .NET kini melakukan hal yang sama. `TypeSafeApiException` tetap menjadi induk seluruh kegagalan HTTP, sehingga satu blok `catch` masih menangkap semuanya.

| HTTP | Python | .NET |
| --- | --- | --- |
| — | `TypeSafeError` | `TypeSafeException` |
| apa saja | `TypeSafeAPIError` | `TypeSafeApiException` |
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

Setiap error HTTP membawa `StatusCode`, `Body`, `Headers`, `Endpoint`, dan `RequestId` (dibaca dari `x-typesafe-request-id`).

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
    // Key salah atau tidak ada — mengulang tidak akan menolong.
}
catch (TypeSafeApiTimeoutException ex)
{
    logger.LogWarning("Timeout setelah {Timeout}", ex.Timeout);
}
```

## Retry

`RetryPolicy` kini persis mengikuti default Python.

| Setelan | Python | .NET |
| --- | --- | --- |
| Percobaan ulang setelah yang pertama | 2 | `MaxRetries = 2` |
| Backoff pertama | 0,5 dtk | `BackoffInitial` |
| Batas atas backoff | 5 dtk | `BackoffMax` |
| Jitter | 0,25 | `BackoffJitter` |
| Status yang diulang | 408, 429, 500–599 | `HttpStatuses` |
| Menghormati `Retry-After` | ya | `RespectRetryAfter` |
| Mengulang error koneksi | ya | `RetryConnectionErrors` |
| Mengulang timeout | ya | `RetryTimeoutErrors` |
| Budget total per panggilan | 30 dtk | `Budget` |

Kebijakan bisa dipasang sekali di client atau ditimpa per panggilan:

```csharp
var options = new TypeSafeOptions { Retry = new RetryPolicy(MaxRetries: 4) };
await client.SystemOneAsync(state, questions, retry: new RetryPolicy(0));   // tanpa retry, hanya kali ini
```

Sebelumnya SDK .NET hanya mengulang 429 dan 529, dan hanya bila kebijakan diberikan eksplisit di tempat pemanggilan.

## Konstanta

`TypeSafeConstants` mencerminkan modul `constants` Python sehingga nama environment variable tidak perlu diketik ulang.

| Konstanta | Nilai |
| --- | --- |
| `ApiKeyEnv` | `TYPESAFE_API_KEY` |
| `BaseUrlEnv` | `TYPESAFE_BASE_URL` |
| `DefaultModelEnv` | `TYPESAFE_DEFAULT_MODEL` |
| `LogLevelEnv` | `TYPESAFE_LOG_LEVEL` |
| `DefaultBaseUrl` | `https://api.typesafe.ai` |
| `DefaultModel` | `jev-latest` |
| `DefaultTimeout` | 10 dtk |

`TypeSafeConstants.SecretHeaders` dan `RedactHeader` tersedia agar nilai header tidak pernah tertulis apa adanya ke log.

## Opsi client

| Parameter Python | .NET |
| --- | --- |
| `api_key` | `TypeSafeOptions.ApiKey` |
| `base_url` | `TypeSafeOptions.Endpoint` |
| `model` | `TypeSafeOptions.DefaultModel` |
| `retry` | `TypeSafeOptions.Retry` |
| `timeout` | `TypeSafeOptions.Timeout` |
| `headers` | `TypeSafeOptions.Headers` |
| `http_client` / `transport` | argumen `HttpClient` pada konstruktor |
| `extra_headers` (per panggilan) | parameter `extraHeaders` |
| `extra_body` (per panggilan) | parameter `extraBody` |

Setiap request membawa `X-TypeSafe-SDK`, `X-TypeSafe-Runtime`, dan — pada percobaan ulang — `X-TypeSafe-Retry-Count`.

## Request

Perilaku berikut datang pada Python SDK 0.7.3–0.7.5 dan disamakan di sini:

- **Validasi sebelum jaringan.** State null, tanpa pertanyaan, pertanyaan tanpa `type` string, `choice` atau `score` tanpa criteria, list atau map criteria kosong, dan `noul` tanpa instruksi maupun hasil yang dideskripsikan, semuanya melempar `TypeSafeException` tanpa mengirim apa pun. Tipe pertanyaan yang tidak dikenal dan field tambahan tetap diteruskan apa adanya, sehingga fitur yang ditambahkan API sebelum SDK ini memodelkannya tetap bisa dipakai.
- **Angka non-finite.** JSON tidak punya token `NaN` atau `Infinity`; keduanya dikirim sebagai string `"NaN"`, `"Infinity"`, dan `"-Infinity"` alih-alih gagal diserialisasi.
- **Kompresi.** Body minimal 1024 byte dikirim dengan `Content-Encoding: zstd`, dikompres sekali dan dipakai ulang oleh setiap retry. `GET /v1/models` tidak punya body dan tidak pernah dikompres.
- **Multipart.** Request bergambar berbentuk `multipart/form-data`: part `request` berisi body JSON, lalu satu part per gambar bernama `asset-0`, `asset-1`, …, yang disebut oleh map `assets` di body. Gambar yang dikirim dengan dua nama dikirim dua kali, karena API mensyaratkan setiap part diklaim tepat satu aset. Key `assets` di `extraBody` menggantikan milik SDK, dan saat itu tidak ada gambar yang dibaca.

## Gambar

Python menamai kelasnya `Image`. Di .NET nama itu bentrok dengan `Avalonia.Controls.Image`, `System.Drawing.Image`, dan `Image` milik WPF di aplikasi-aplikasi yang justru memakai SDK ini, jadi di sini namanya `ImageAsset`; `Asset` tetap. Factory-nya mencerminkan konstruktor Python: `FromBytes` (byte mentah dan buffer), `FromFile` (path), `FromBase64`, dan `FromStream`. Tidak ada `from_pil`; enkode gambar `System.Drawing`/`SkiaSharp`/`ImageSharp` ke byte PNG atau JPEG lalu pakai `FromBytes`.

Di Python, `instructions` dan `criteria` menerima `Image` secara langsung karena bertipe `Content`. Schema .NET mempertahankan `string` untuk field tersebut, jadi schema bertipe merujuk gambar lewat alias atau rujukannya di dalam teks — `new BoxSchema($"The dog next to {photo}")` dengan aset yang dipasang atau dilewatkan. Dictionary pertanyaan mentah boleh memuat `ImageAsset` di mana saja, persis seperti Python.

Selama gambar masih eksperimental, API menjawab `400` kecuali request membawa `TypeSafe-Danger-Zone-Images: true`. Seperti Python SDK, SDK ini tidak mengirimnya sendiri; namanya tersedia sebagai `TypeSafeConstants.ImagesOptInHeader`.

## Perbedaan yang disengaja

### Sync vs async
Python menyediakan client sinkron dan asinkron. .NET tidak memerlukan pemisahan itu: `SystemOneAsync` adalah satu-satunya API, dan pemanggil yang ingin memblokir bisa memakai `.GetAwaiter().GetResult()`. Menambah permukaan sinkron paralel hanya menggandakan API tanpa manfaat.

### `response_model`
`system_one(response_model=…)` pada Python memvalidasi ulang response ke model Pydantic pilihan pemanggil. Padanan .NET-nya adalah mendeserialisasi `SystemOneResponse` sendiri atau memetakannya ke tipe Anda; overload generik hanya menambah biaya refleksi tanpa menambah keamanan yang belum diberikan sistem tipe.

### Detail transport
Python SDK mematok keep-alive connection pool 30 dtk, menambal bug TLS `truststore`, menyediakan extra `http2`, dan memangkas body yang di-log. Tidak satu pun berlaku untuk `HttpClient`: `SocketsHttpHandler` sudah mem-pool koneksi, memakai certificate store OS, dan mendukung HTTP/2 lewat `HttpClient.DefaultRequestVersion`; SDK ini tidak pernah me-log body request.

### Simulator
Simulator tidak punya padanan di Python SDK. Ia menjawab pertanyaan `choice`, `noul`, dan `score` secara lokal sehingga test, CI, dan demo berjalan tanpa key dan tanpa jaringan. Sifatnya leksikal, bukan semantik: kata langka pada kriteria diberi bobot lebih besar daripada kata umum, negasi dihormati (`no nesting` tidak dihitung sebagai nesting), dan bila tidak ada sinyal ia mengembalikan distribusi rata dengan confidence rendah alih-alih menebak. Simulator tidak bisa melihat gambar, jadi pertanyaan `box` mendapat seluruh bingkai dengan confidence 0. Lihat [panduan SDK](sdk-guide.id.md) untuk kapan simulator layak dipercaya.

## Memverifikasi kesejajaran

`TypeSafeSdk.Tests/ParityTests.cs` dan `UpstreamSyncTests.cs` menguji setiap butir pada halaman ini: pemetaan status, `request_id`, `retry-after`, error timeout dan koneksi, status yang diulang, batas jitter, header identitas, header dan body tambahan, konstanta, bentuk wire tiap tipe pertanyaan, jawaban box, validasi sebelum jaringan, angka non-finite, kompresi, serta tata letak multipart untuk setiap cara mengirim gambar.

```bash
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~ParityTests|FullyQualifiedName~UpstreamSyncTests"
```

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil.
