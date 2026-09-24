# Jev Gallery

Katalog desktop Avalonia berisi lima belas use case TypeSafe yang bisa dijalankan. Setiap specimen benar-benar memanggil `SystemOneAsync` — ke simulator lokal secara default, atau ke `api.typesafe.ai` bila Anda menyediakan API key — lalu menampilkan jawaban beserta distribusi probabilitasnya dan kode C# persis yang menghasilkannya.

![Jev Gallery menjalankan specimen tiket dukungan](images/jev-gallery.png)

## Menjalankan

```bash
dotnet run --project TypeSafe.JevGallery
```

Jendela terbagi tiga: rail domain di kiri, katalog specimen di tengah, dan panel detail di kanan tempat Anda menyunting input, menjalankan specimen, dan membaca kodenya.

## Katalog

Setiap specimen memiliki nomor akses berupa huruf domain dan indeks agar bisa dirujuk tanpa ambigu.

| # | Specimen | Pertanyaan | Yang ditunjukkan |
| --- | --- | --- | --- |
| G-01 | NPC intent router | choice | Deskripsi `criteria` yang kaya jauh menajamkan jawaban dibanding label telanjang |
| G-02 | Chat toxicity gate | noul | Nilai 0..1 membuat tiap server memilih ambangnya sendiri |
| G-03 | Quest difficulty tier | score | Nilai harapan pada skala berurutan, langsung bisa dipakai sebagai angka |
| E-01 | Essay rubric band | score ×2, noul | Beberapa penilaian independen dalam satu request |
| E-02 | Student question triage | choice, noul | Perutean sekaligus sinyal prioritas |
| E-03 | Reading level fit | score, noul | Menilai bacaan terhadap jenjang sasaran |
| W-01 | Support ticket triage | choice, noul | Use case kanonik, plus `RequestId` untuk observability |
| W-02 | Meeting action items | noul ×2 | Mendeteksi komitmen dan apakah pemiliknya disebut |
| W-03 | CV screening signal | score, choice | CV yang sengaja ambigu — perhatikan confidence rendah pada `fit` |
| S-01 | Abstract method tagging | choice, noul | Membangun indeks literatur yang bisa ditelusuri |
| S-02 | Field observation coding | choice, score | Mengubah catatan lapangan bebas menjadi kode teragregasi |
| S-03 | Anomaly severity | score, noul | Keparahan dan keputusan "bangunkan orang" yang terpisah |
| M-01 | Legal move selection | choice | Hanya langkah legal yang jadi kriteria, sehingga jawaban ilegal mustahil |
| M-02 | Traffic incident dispatch | choice, noul | Probabilitas menyingkap insiden multi-unit yang disembunyikan label tunggal |
| M-03 | Sensor state machine | choice, noul | Kosakata telemetri di dalam deskripsi kriteria |

## Membaca distribution strip

Tiap jawaban digambar sebagai pita horizontal yang lebar tiap segmennya adalah massa probabilitas antar kriteria. Segmen terpilih berwarna hijau moss, sisanya rust redup. Inilah inti galeri: TypeSafe tidak mengembalikan label telanjang melainkan sebuah distribusi, sehingga kasus ambigu langsung terlihat alih-alih tersembunyi di balik satu kata.

- **Choice** menampilkan satu segmen per label, plus confidence.
- **Noul** menampilkan nilai 0..1 yang dibelah menjadi true dan false, serta apakah terbaca true pada ambang 0,50.
- **Score** menampilkan satu segmen per level, nilai harapan, dan label legend terdekat.

W-03 dan M-01 layak diperhatikan: keduanya sengaja menjawab dengan confidence rendah, dan kode contoh W-03 menunjukkan cara melempar penolakan berconfidence rendah ke peninjau manusia alih-alih menindaklanjutinya.

## Mode simulator versus mode live

`app.config.json` di samping executable mengatur modenya.

```json
{
  "ApiKey": "",
  "Endpoint": "https://api.typesafe.ai",
  "DefaultModel": "jev-latest",
  "UseSimulator": true,
  "Motion": true
}
```

**Mode simulator** (default) menjawab secara lokal tanpa key dan tanpa jaringan. Sifatnya leksikal, bukan semantik: kata langka pada kriteria diberi bobot lebih besar daripada kata umum, negasi dihormati, dan bila tidak ada sinyal ia mengembalikan distribusi rata alih-alih menebak. Cukup untuk memperlihatkan bentuk sebuah jawaban, dan inilah yang dipakai test regresi.

**Mode live** — ubah `UseSimulator` menjadi `false` — memanggil API sungguhan. API key tidak pernah disimpan di repo: galeri membaca `ApiKey` pada konfigurasi, lalu `TYPESAFE_API_KEY`, lalu file yang ditunjuk `TYPESAFE_API_KEY_FILE`.

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\secrets\TypeSafeApiKey.txt'
dotnet run --project TypeSafe.JevGallery
```

![Jev Gallery menjawab dari API live](images/jev-gallery-live.png)

Rail menampilkan `LIVE API` berwarna brass saat galeri berbicara dengan endpoint sungguhan. Perbedaannya layak dilihat: pada G-03 simulator berhenti di `steady`, sementara model live mengembalikan `2.18 — demanding`.

## Catatan desain

Palet memakai nuansa casing instrumen yang hangat, bukan dashboard biru gelap yang biasa: bark `#191714`, bone `#E6DFCE`, moss `#86A22F` untuk jawaban terpilih, rust `#B4522E` untuk sisa probabilitas, dan brass `#D9A62B` yang dipakai sekali saja sebagai penanda aktif. Huruf display memakai Bahnschrift, body Segoe UI, dan seluruh label struktural serta angka memakai Cascadia Mono. Satu-satunya momen animasi adalah distribution strip yang menyapu terbuka saat specimen dijalankan; bisa dimatikan lewat sakelar **Animate results**.

## Pengujian

Katalog ini bukan sekadar teks. `TypeSafeSdk.Tests/GalleryCatalogTests.cs` menautkan `Specimens.cs` secara langsung dan menjalankan setiap specimen lewat SDK, memastikan tiap specimen menjawab seluruh pertanyaannya, jawaban choice selalu berasal dari kriterianya sendiri, dan jawaban demo untuk G-01, E-02, W-01, S-01, S-02, serta M-03 tetap benar.

```bash
dotnet test TypeSafeSdk.Tests/TypeSafeSdk.Tests.csproj --filter "FullyQualifiedName~GalleryCatalogTests"
```

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil.
