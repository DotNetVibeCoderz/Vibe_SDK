# Panduan Penggunaan SDK

**Bahasa Indonesia** · [English](sdk-guide.en.md)

Panduan ini mengikuti Python SDK resmi TypeSafe.

## System One dengan beberapa question

```csharp
await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
var questions = new Dictionary<string, object>
{
    ["billing"] = new NoulSchema("Is this about billing?").ToApiQuestion(),
    ["tone"] = Choice.Create("calm", "angry").ToApiQuestion(),
    ["urgency"] = new ScoreSchema("How urgent is this?", "low", "medium", "high").ToApiQuestion()
};
var result = await client.SystemOneAsync("I was charged twice. Please help ASAP.", questions, "jev-latest");
```

Akses hasil berdasarkan tipe:

```csharp
var billing = result.Nouls["billing"].Noul;
var tone = result.Choices["tone"].Choice;
var urgency = result.Scores["urgency"].Score;
```

## Pemilihan model

```csharp
var options = new TypeSafeOptions
{
    ApiKey = "from-secret-manager",
    DefaultModel = "jev-latest"
};
await using var client = new TypeSafeClient(options);
```

## Daftar model

```csharp
var models = await client.Models.ListAsync(cancellationToken: ct);
foreach (var model in models)
    Console.WriteLine($"{model.Name} — {model.Description} ({model.ReleaseDate})");
```

`ListModelsResponse` bisa langsung dipakai sebagai list dan juga menyediakan `.Models`. Saat dokumen ini ditulis, API live mengembalikan `jev-latest` dan `jev-preview`.

## Retry, timeout, dan header

Default mengikuti Python SDK: dua kali percobaan ulang, backoff awal 0,5 dtk yang digandakan sampai batas 5 dtk, jitter 25%, mengulang status 408, 429, dan seluruh 5xx, menghormati `Retry-After`, di dalam budget 30 dtk untuk keseluruhan panggilan.

```csharp
var options = new TypeSafeOptions
{
    ApiKey = key,
    Timeout = TimeSpan.FromSeconds(20),
    Retry = new RetryPolicy(MaxRetries: 4, BackoffInitial: TimeSpan.FromMilliseconds(250)),
    Headers = { ["X-Tenant"] = "gravicode" }
};
await using var client = new TypeSafeClient(options);
```

Apa pun yang dipasang di client bisa ditimpa untuk satu panggilan:

```csharp
var result = await client.SystemOneAsync(
    state,
    questions,
    model: "jev-preview",
    retry: new RetryPolicy(0),                 // jangan ulang yang satu ini
    timeout: TimeSpan.FromSeconds(60),
    extraHeaders: new Dictionary<string, string> { ["X-Trace"] = traceId },
    extraBody: new Dictionary<string, object?> { ["metadata"] = new { tenant = "acme" } },
    cancellationToken: cancellationToken);
```

Setiap request membawa `X-TypeSafe-SDK` dan `X-TypeSafe-Runtime`; percobaan ulang menambahkan `X-TypeSafe-Retry-Count`. Pakai `TypeSafeConstants.RedactHeader` sebelum menulis header apa pun ke log — fungsi itu menyensor `Authorization`, `x-api-key`, cookie, dan sisa isi `TypeSafeConstants.SecretHeaders`.

## Raw question dictionary

Question dictionary mentah berguna untuk fitur API baru:

```csharp
var questions = new Dictionary<string, object>
{
    ["billing"] = new
    {
        type = "noul",
        instructions = "About billing?",
        weight = 2 // field tambahan diteruskan ke API
    }
};
```

## Typed response

Response resmi dapat diakses strongly typed melalui `Answers`, `Nouls`, `Choices`, dan `Scores`:

```csharp
Answer answer = result.Answers["billing"];
if (answer is NoulAnswer noul) Console.WriteLine(noul.Noul);
if (answer is ChoiceAnswer choice) Console.WriteLine(choice.Choice);
if (answer is ScoreAnswer score) Console.WriteLine(score.Score);
```

## Error handling

Setiap kegagalan HTTP melempar exception bertipe. `TypeSafeApiException` adalah induknya sehingga satu `catch` tetap menangkap semuanya, dan tiap exception membawa `StatusCode`, `Body`, `Headers`, `Endpoint`, serta `RequestId`.

```csharp
try
{
    await client.SystemOneAsync(state, questions, cancellationToken: ct);
}
catch (TypeSafeAuthenticationException)
{
    // 401 — key salah atau tidak ada. Mengulang tidak menolong.
}
catch (TypeSafeRateLimitException ex)
{
    // 429 — server sudah memberi tahu berapa lama harus menunggu.
    await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(1), ct);
}
catch (TypeSafeUnprocessableEntityException ex)
{
    logger.LogError("Payload ditolak: {Body} (request {RequestId})", ex.Body, ex.RequestId);
}
catch (TypeSafeApiTimeoutException ex)
{
    logger.LogWarning("Timeout setelah {Timeout}", ex.Timeout);
}
catch (TypeSafeApiConnectionException)
{
    // Tidak ada response sama sekali: DNS, TLS, atau jaringan.
}
catch (TypeSafeApiException ex)
{
    logger.LogError("HTTP {Status} dari {Endpoint}", ex.StatusCode, ex.Endpoint);
}
```

Tabel lengkap status ke exception ada di [Kesejajaran dengan Python SDK](parity-python.id.md#error).

## Simulator

`Simulator = true` menjawab pertanyaan `choice`, `noul`, dan `score` secara lokal, tanpa key dan tanpa jaringan, sehingga unit test dan CI bersifat deterministik.

```csharp
await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
```

Pahami dulu sifatnya sebelum mempercayainya. Simulator bersifat **leksikal, bukan semantik**: ia mencocokkan kata pada kriteria dengan kata pada state, memberi bobot lebih besar pada kata yang hanya muncul di satu kriteria dibanding yang muncul di semuanya, mengabaikan penyebutan yang dinegasikan (`no nesting` tidak dihitung sebagai nesting), dan bila sama sekali tidak menemukan sinyal ia mengembalikan distribusi rata dengan confidence rendah alih-alih menebak. Ia tepat untuk memastikan pipa dan bentuk schema Anda benar, bukan pengganti penilaian model: pada deskripsi quest yang sama simulator menjawab `steady`, sedangkan model live menjawab `demanding`.

Beri kriteria Anda deskripsi sungguhan, maka simulator maupun model live sama-sama bekerja lebih baik:

```csharp
["state"] = Choice.Create(new Dictionary<string, string?>
{
    ["nominal"]   = "Readings steady and within limits, no trend",
    ["degrading"] = "Readings drifting: pressure falling or temperature rising over time",
    ["fault"]     = "Readings breach safe limits or an alarm is active",
    ["offline"]   = "No telemetry received from the asset"
}, "What state is this asset in?")
```

## Gambar dan pertanyaan box

Request bisa membawa gambar. `ImageAsset` membungkus byte gambar, file, teks base64, atau stream, dan `BoxSchema` menanyakan di mana letak sesuatu. Gambar butuh model vision-language, dan selama dukungan gambar masih eksperimental API juga meminta header opt-in.

```csharp
var options = new TypeSafeOptions
{
    ApiKey = key,
    Headers = { [TypeSafeConstants.ImagesOptInHeader] = "true" }   // TypeSafe-Danger-Zone-Images
};
await using var client = new TypeSafeClient(options);

var photo = ImageAsset.FromFile("rak.jpg");
var result = await client.SystemOneAsync(
    "<photo>",
    new Dictionary<string, object>
    {
        ["dog"]     = new BoxSchema("The dog"),
        ["damaged"] = new NoulSchema("Is any product on the shelf damaged?")
    },
    model: "jev-preview",
    assets: new Dictionary<string, Asset> { ["<photo>"] = photo });

var box = result.Boxes["dog"].Box;                 // Top, Bottom, Left, Right sebagai pecahan 0..1
var (x, y, w, h) = box.ToPixels(1920, 1080);       // kembali ke piksel untuk digambar
```

Ada tiga cara memasukkan gambar ke request, dan ketiganya boleh dicampur:

| Cara | Bagaimana | Dikirim sebagai |
| --- | --- | --- |
| Lewat alias | `assets: { ["<photo>"] = photo }`, lalu tulis `<photo>` di state, instruksi, atau criteria | alias-nya |
| Lewat penempatan | letakkan objek aset langsung di state, misalnya `new { photo, caption }` | rujukannya |
| Lewat rujukan di teks | `using (client.AttachAssets(photo))` lalu `$"Product: {photo}"` | rujukannya |

`photo.ToString()` mengembalikan rujukan unik seperti `<image:3f2a…>`, karena itu interpolasi string bekerja. Rujukan ke gambar yang tidak dilewatkan, tidak ditempatkan, dan tidak dipasang dikirim sebagai teks biasa, tanpa gambarnya.

SDK membaca setiap gambar sekali, lalu mengirim request sebagai `multipart/form-data`: body JSON menjadi part pertama bernama `request`, dan setiap gambar menyusul sebagai part tersendiri. PNG dan JPEG diberi label dari signature filenya; format lain dikirim sebagai data biner dan dideteksi API. File yang tidak bisa dibaca atau data kosong gagal dengan `TypeSafeException` yang menyebut alias-nya, sebelum apa pun terkirim.

Simulator tidak bisa melihat. Pertanyaan box dijawab dengan seluruh bingkai dan confidence 0 — jujur, tetapi tidak berguna — jadi uji hasil box hanya terhadap API live.

## Kompresi request

Body berukuran 1 KB ke atas dikompres dengan zstd dan dikirim dengan `Content-Encoding: zstd`, default yang sama dengan Python SDK. Body yang lebih kecil muat dalam satu segmen TCP, jadi mengompresnya hanya memakan CPU tanpa menghemat apa pun. Body dikompres sekali dan byte yang sama dipakai ulang oleh setiap retry.

Sebagian gateway dan endpoint tidak resmi belum menerima body terkompresi. Matikan untuk mereka:

```csharp
var options = new TypeSafeOptions { Endpoint = gatewayUrl, Compression = RequestCompression.None };
```

## Environment variable

Namanya tersedia sebagai konstanta di `TypeSafeConstants` agar tidak perlu diketik ulang.

- `TYPESAFE_API_KEY` — `TypeSafeConstants.ApiKeyEnv`
- `TYPESAFE_BASE_URL` — default `https://api.typesafe.ai`
- `TYPESAFE_DEFAULT_MODEL` — default `jev-latest`
- `TYPESAFE_LOG_LEVEL` — mengisi `TypeSafeOptions.LogLevel`
- `TYPESAFE_SIMULATOR=true`
- `TYPESAFE_API_KEY_FILE` — path file key yang diparsing `TypeSafeApiKeyLoader`

```csharp
var options = TypeSafeOptions.FromEnvironment();
```

Dibuat oleh Gravicode Studios, dipimpin Kang Fadhil.
