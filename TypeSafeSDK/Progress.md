# Development Progress

## 2026-09-25
- TypeSafeAppGen ditulis ulang dari kerangka menjadi code editor yang berfungsi sesuai `requirements.md`:
  - Jack terhubung ke LLM sungguhan lewat Semantic Kernel: OpenAI (plus endpoint kompatibel), Azure OpenAI,
    Claude (SDK resmi `Anthropic` via `IChatClient`), Gemini, dan Ollama. Streaming, auto function calling,
    batas putaran tool, dan pesan error yang menunjukkan cara memperbaikinya.
  - Kernel functions: workspace (baca/tulis/edit/cari file, dibatasi ke folder proyek), project (build dengan
    diagnostic, test, NuGet, `dotnet new`, template), web (Tavily, scrape), math (parser ekspresi sendiri),
    time, dan typesafe (referensi SDK + klasifikasi simulator).
  - Editor AvaloniaEdit bertab dengan highlighting TextMate tema "Patina", line number show/hide, find/replace,
    go to line, format code (Roslyn, JSON, XML/XAML), auto-reload saat file berubah di disk.
  - Code explorer dengan lazy loading, context menu, dan FileSystemWatcher.
  - Build/Run/Stop/Deploy (`dotnet publish` dengan pilihan runtime), panel Output/Problems/Logs, status bar.
  - Panel Jack: pemilih model, lampiran gambar (tombol dan drag-drop), Ctrl+Enter, Stop, Clear thread,
    resize dan hide/show; balasan dirender markdown dengan blok kode Copy/Insert dan chip tool call.
  - Dialog New Project (Blank / From Template) dengan 12 template yang semuanya build tanpa warning:
    Wireframe Studio, Terrain Flyover, Particle Fireworks, Motion Lab, Snake Arcade, Brick Breaker,
    Life Automaton, Orbit Sandbox, Outbreak Simulator, Live Ops Dashboard (Blazor), Task Board API,
    Ticket Triage (TypeSafe SDK).
  - Dialog Settings untuk seluruh `app.config.json`, termasuk Test connection; format konfigurasi lama dimigrasikan.
- Diuji langsung dengan Azure OpenAI `gpt-5-mini`: Jack mengedit file lalu build berhasil, dan memperbaiki error build yang disisipkan.
- Avalonia di AppGen dan template dinaikkan ke 11.3.20 (Tmds.DBus.Protocol 0.21.3, bebas advisory NU1903).
- Project test baru `TypeSafeAppGen.Tests`: 77 test offline. Total solution 173 test lulus.
- Dokumentasi bilingual `app-generator` dengan tujuh screenshot.

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
