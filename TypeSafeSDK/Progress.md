# Development Progress

## 2026-09-24
- Jev Gallery ditambahkan: aplikasi Avalonia berisi 15 specimen use case (game, edukasi, pekerjaan,
  sains, simulasi), masing-masing memanggil `SystemOneAsync` sungguhan dan menampilkan distribution
  strip probabilitas, confidence, serta kode C# yang persis menghasilkannya.
- SDK disejajarkan dengan Python SDK resmi setelah membandingkan permukaan publiknya:
  - 12 exception bertipe menggantikan satu `TypeSafeApiException`, lengkap dengan `StatusCode`,
    `Body`, `Headers`, `Endpoint`, dan `RequestId`.
  - `RetryPolicy` mengikuti default Python: 2 retry, backoff 0,5 dtk sampai 5 dtk, jitter 0,25,
    status 408/429/5xx, menghormati `Retry-After`, budget 30 dtk. Retry kini bisa dipasang di client.
  - Timeout per request, header kustom di client maupun per panggilan, dan `extraBody`.
  - `TypeSafeConstants`, `ListModelsResponse`, `ModelMetadata.ReleaseDate`, `Usage` nullable,
    `ScoreAnswer.LegendByLevel` dan `NearestLabel`.
  - Bug diperbaiki: schema mentah yang dilewatkan sebagai `questions` dulu diserialisasi sebagai
    record .NET, bukan sebagai bentuk wire API.
- Simulator dirombak agar menjawab choice, noul, dan score secara umum: bobot mirip IDF supaya kata
  yang muncul di semua kriteria tidak meratakan distribusi, penanganan negasi, penilaian atas nilai
  state bukan nama field, dan abstain dengan distribusi rata ketika tidak ada sinyal.
- Kontrak diverifikasi langsung ke API live: `models`, `classify`, `noul`, `score`, dan panggilan
  multi-question. API mengembalikan `model` sebagai build terselesaikan (`jev-1.13.0`) dan
  `release_date` sebagai timestamp ISO penuh; keduanya terparsing benar.
- Paket dirilis sebagai `Gravicode.TypeSafeSdk` 1.1.0. `FrameworkReference` ke
  Microsoft.AspNetCore.App diganti `Microsoft.Extensions.Logging.Abstractions` agar paket bisa
  dipakai aplikasi console dan desktop, bukan hanya aplikasi web; hasil pack diuji dari console app bersih.
- Dokumentasi bilingual ditambah: `parity-python`, `jev-gallery`, dan `nuget`, masing-masing ID dan EN.
- 96 test lulus, naik dari 21.

## 2026-09-19
- TypeSafe Board Games WPF diperbaiki dari fondasi menjadi game playable.
- Tic Tac Toe, Othello, dan Connect Four memiliki aturan board, legal move validation, win/draw, score, dan reset round/match.
- Othello mendukung flipping 8 arah, pass, dan perhitungan disc akhir.
- Connect Four mendukung gravity column dan deteksi empat sebaris.
- UX diperbarui: board proporsional, legal move highlight, status turn, scoreboard draw, live/simulator mode indicator, dan AI note yang informatif.
- AI TypeSafe hanya menerima daftar legal moves; local tactical fallback menjamin AI selalu membuat langkah valid ketika simulator/API gagal.
- Build solution berhasil tanpa warning/error; 21 unit test SDK lulus.

Dibuat oleh Gravicode Studios dipimpin Kang Fadhil.
