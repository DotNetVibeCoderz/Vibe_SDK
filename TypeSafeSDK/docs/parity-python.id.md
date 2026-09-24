# Kesejajaran dengan Python SDK resmi

**Bahasa Indonesia** · [English](parity-python.en.md)

SDK .NET ini adalah port tidak resmi dari [`typesafe-ai/typesafe-sdk-python`](https://github.com/typesafe-ai/typesafe-sdk-python). Halaman ini mencatat apa yang dibandingkan, apa yang kini sejajar, dan apa yang sengaja berbeda karena .NET bukan Python.

## Permukaan publik, berdampingan

| Python SDK | SDK .NET | Status |
| --- | --- | --- |
| `TypeSafeClient` / `AsyncTypeSafeClient` | `TypeSafeClient` (async) | Disesuaikan — lihat [Sync vs async](#sync-vs-async) |
| `client.system_one(state, questions, …)` | `client.SystemOneAsync(state, questions, …)` | Sejajar |
| `client.models.list()` | `client.Models.ListAsync()` | Sejajar |
| `Choice`, `Score`, `Noul` | `ChoiceSchema`, `ScoreSchema`, `NoulSchema` | Sejajar |
| `NoulCriteria` (`true` / `false`) | `NoulSchema(instructions, True, False)` | Sejajar |
| `ChoiceModel.criteria` label → deskripsi | `Choice.Create(IReadOnlyDictionary<string, string?>)` | Sejajar |
| Alias tipe `Question`, `Questions`, `QuestionModel` | `IQuestionSchema`, `QuestionsSchema`, `QuestionPayload.Normalize` | Disesuaikan |
| `SystemOneResponse` dengan `.nouls` / `.choices` / `.scores` | Sama, plus `.ChoiceAnswers` dan `.RequestId` | Sejajar + |
| `Answer`, `NoulAnswer`, `ChoiceAnswer`, `ScoreAnswer` | Nama sama, `Answer` berupa record polimorfik | Sejajar |
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

## Perbedaan yang disengaja

### Sync vs async
Python menyediakan client sinkron dan asinkron. .NET tidak memerlukan pemisahan itu: `SystemOneAsync` adalah satu-satunya API, dan pemanggil yang ingin memblokir bisa memakai `.GetAwaiter().GetResult()`. Menambah permukaan sinkron paralel hanya menggandakan API tanpa manfaat.

### `response_model`
`system_one(response_model=…)` pada Python memvalidasi ulang response ke model Pydantic pilihan pemanggil. Padanan .NET-nya adalah mendeserialisasi `SystemOneResponse` sendiri atau memetakannya ke tipe Anda; overload generik hanya menambah biaya refleksi tanpa menambah keamanan yang belum diberikan sistem tipe.

### Simulator
Simulator tidak punya padanan di Python SDK. Ia menjawab pertanyaan `choice`, `noul`, dan `score` secara lokal sehingga test, CI, dan demo berjalan tanpa key dan tanpa jaringan. Sifatnya leksikal, bukan semantik: kata langka pada kriteria diberi bobot lebih besar daripada kata umum, negasi dihormati (`no nesting` tidak dihitung sebagai nesting), dan bila tidak ada sinyal ia mengembalikan distribusi rata dengan confidence rendah alih-alih menebak. Lihat [panduan SDK](sdk-guide.id.md) untuk kapan simulator layak dipercaya.

## Memverifikasi kesejajaran

`TypeSafeSdk.Tests/ParityTests.cs` menguji setiap butir pada halaman ini: pemetaan status, `request_id`, `retry-after`, error timeout dan koneksi, status yang diulang, batas jitter, header identitas, header dan body tambahan, konstanta, serta bentuk wire tiap tipe pertanyaan.

```bash
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~ParityTests"
```

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil.
