# TypeSafe App Generator

**Bahasa Indonesia** · [English](app-generator.en.md)

Code editor desktop Avalonia dengan asisten AI **Jack — The Code Bender**. Anda menjelaskan aplikasi yang diinginkan; Jack menulis file-nya ke proyek yang terbuka, menjalankan `dotnet build`, membaca error, dan memperbaikinya sampai build berhasil. Semua file yang disentuh Jack langsung terbuka di editor.

![TypeSafe App Generator dengan proyek terbuka](images/appgen-workspace.png)

## Menjalankan

```bash
dotnet run --project TypeSafeAppGen
```

Aplikasi terbuka dalam keadaan maximized dan otomatis membuka proyek terakhir. Tanpa proyek, area editor menampilkan halaman awal berisi aksi New/Open, proyek terbaru, dan daftar shortcut.

## Tata letak

| Area | Isinya |
| --- | --- |
| Menu & toolbar | New Project, Open Folder/File, Save, Format Code, Go to Line, Build, Run, Stop, Deploy, Settings, serta tombol show/hide untuk Explorer, panel Output, dan panel Jack |
| Explorer (kiri) | Pohon file seperti VS Code, folder dimuat saat dibuka, `bin`/`obj`/`.git` disembunyikan. Klik kanan: New File, New Folder, Rename, Delete, Copy Path, Reveal in File Explorer, Ask Jack About This File. Diperbarui otomatis saat file berubah di disk |
| Editor (tengah) | AvaloniaEdit bertab dengan syntax highlighting TextMate (tema "Patina"), line number yang bisa di-show/hide, Find/Replace, undo per tab, word wrap, dan zoom. Tab yang berubah bertanda titik; file yang diubah dari luar (misalnya oleh Jack) dimuat ulang otomatis |
| Panel bawah | **Output** (stream `dotnet build/run/publish`), **Problems** (error dan warning hasil parse, klik dua kali untuk lompat ke baris), dan **Logs** (aktivitas aplikasi dan setiap tool call Jack) |
| Panel Jack (kanan) | Pemilih model di atas, thread chat, lampiran gambar, tombol Clear thread, Send/Stop. Lebar bisa diubah dengan menyeret splitter dan disimpan ke konfigurasi |
| Status bar | Status proses, jumlah error/warning, posisi Ln/Col, bahasa file, model aktif, dan atribusi Gravicode Studios |

## Jack — The Code Bender

![Jack mengedit file dan menjalankan build](images/appgen-jack-edit.png)

- Kirim dengan **Ctrl+Enter** atau tombol Send. Saat Jack bekerja, tombol berubah menjadi **Stop**.
- **Ctrl+L** memindahkan fokus ke input Jack, **Ctrl+I** meminta Jack menjelaskan file yang sedang dibuka.
- Lampirkan gambar (PNG, JPG, GIF, WebP, maksimal 5 MB) lewat tombol klip atau dengan menyeret file ke panel. Jack bisa membangun UI dari screenshot atau sketsa.
- Setiap tool call tampil sebagai chip tembaga di dalam balasan (misalnya `workspace.edit_file MainWindow.cs`), lalu berubah menjadi centang atau tanda peringatan saat selesai.
- Blok kode di balasan punya tombol **Copy** dan **Insert at cursor**.
- **Clear thread** menghapus percakapan; file yang sudah ditulis tidak ikut terhapus.

Jack bekerja dalam loop: memeriksa proyek, menulis file, build, lalu memperbaiki error. Contoh nyata saat pengujian dengan `gpt-5-mini`: diminta "Build the project and fix every error", Jack membaca error CS1002, mengedit file, lalu build ulang sampai sukses.

![Jack memperbaiki error build](images/appgen-jack-fix.png)

### Kernel functions

Semua fungsi dibangun dengan Semantic Kernel dan diekspos ke model sebagai tools. Akses file dibatasi ke folder proyek: path relatif maupun absolut yang keluar dari proyek ditolak.

| Plugin | Fungsi |
| --- | --- |
| `workspace` | `get_project_info`, `list_files`, `read_file` (bernomor baris, bisa per rentang), `write_file`, `edit_file` (ganti potongan unik, toleran line ending), `create_folder`, `delete_path`, `search_in_files` (regex), `get_active_editor`, `open_in_editor` |
| `project` | `build_project` (mengembalikan setiap error beserta file dan barisnya), `run_tests`, `add_nuget_package`, `dotnet_new` (template bawaan yang diizinkan saja), `list_templates`, `create_project_from_template` |
| `web` | `search_internet` (Tavily), `scrape_web_page` (teks bersih tanpa script dan markup) |
| `math` | `calculate` (parser ekspresi sendiri, tanpa eval dinamis), `statistics` |
| `time` | `get_date_time` (zona waktu IANA/Windows), `date_difference` (termasuk hari kerja), `add_to_date` |
| `typesafe` | `typesafe_sdk_reference` (cara pakai SDK yang benar), `typesafe_classify` (klasifikasi offline via simulator) |

Exception di dalam tool dikembalikan ke model sebagai pesan `Error: …` agar Jack bisa memperbaiki langkahnya, bukan menggagalkan seluruh giliran. Jumlah putaran tool per pesan dibatasi (default 24) supaya biaya dan waktu terkendali.

## Penyedia LLM

| Penyedia | Konektor | Catatan |
| --- | --- | --- |
| OpenAI | Semantic Kernel OpenAI | Endpoint bisa diganti ke layanan kompatibel OpenAI (DeepSeek, Groq, LM Studio) |
| Azure OpenAI | Semantic Kernel Azure OpenAI | Model = nama deployment |
| Claude | SDK resmi `Anthropic` via `IChatClient` → `AsChatCompletionService()` | Default `claude-opus-5` |
| Gemini | Semantic Kernel Google | Kunci Google AI Studio |
| Ollama | Semantic Kernel Ollama | Lokal, tanpa API key |

Model penalaran (OpenAI gpt-5/o-series, Claude generasi 4.7 ke atas) menolak parameter `temperature`, jadi parameter itu tidak dikirim untuk model tersebut.

Kesalahan provider diterjemahkan menjadi pesan yang bisa ditindaklanjuti, misalnya "Claude rejected the API key (401). Check it in Settings → Models." atau "Could not reach Ollama at http://localhost:11434. Start it with `ollama serve`…", lengkap dengan tombol **Open settings** bila masalahnya ada di konfigurasi.

## Konfigurasi

Semua pengaturan disimpan di `app.config.json` di folder aplikasi dan bisa diubah dari **Tools › Settings** (Ctrl+,):

![Dialog Settings](images/appgen-settings.png)

- **Models**: penyedia aktif; per penyedia: daftar model di picker, model default, API key, dan endpoint; tombol **Test connection** mengirim satu permintaan singkat.
- **Jack**: temperature, max output tokens, max tool rounds, system prompt (dengan tombol restore default).
- **Editor**: ukuran font, line number, word wrap, auto save.
- **Tools**: Tavily API key untuk pencarian internet, folder default proyek baru.

Lebar/tinggi panel, visibilitas panel, dan proyek terbaru juga disimpan. Format lama (`Provider`, `Model`, `ApiKey`, `Endpoint` di level atas) dimigrasikan otomatis. Bila file rusak, aplikasi menyimpan cadangan `.bak` lalu memakai default.

> API key tersimpan sebagai teks biasa di `app.config.json` pada folder output build. File sumber di repositori tidak pernah berisi key.

## Proyek baru: Blank atau From Template

![Galeri template](images/appgen-new-project.png)

**Blank** membuat proyek console .NET 10 kosong untuk dikembangkan bersama Jack. **From template** menyediakan aplikasi yang langsung bisa di-build dan dijalankan:

| Kategori | Template | Use case |
| --- | --- | --- |
| 3D Graphics | Wireframe Studio | Viewer wireframe 3D dengan proyeksi perspektif, orbit, dan zoom |
| 3D Graphics | Terrain Flyover | Terrain prosedural dengan painter's algorithm dan kamera terbang |
| Animation | Particle Fireworks | Sistem partikel dengan gravitasi, drag, dan trail |
| Animation | Motion Lab | Perbandingan kurva easing untuk desain gerak UI |
| Game | Snake Arcade | Game loop, input buffering, high score |
| Game | Brick Breaker | Breakout dengan kontrol sudut dari titik pantul paddle |
| Simulator | Life Automaton | Game of Life dengan menggambar sel dan kontrol kecepatan |
| Simulator | Orbit Sandbox | Gravitasi N-body dengan integrator velocity Verlet |
| Simulator | Outbreak Simulator | Epidemi SIR berbasis agen dengan grafik langsung |
| Web | Live Ops Dashboard | Blazor Server dengan feed metrik background dan sparkline |
| Web | Task Board API | Minimal API dengan typed results, validasi, OpenAPI, dan file `.http` |
| AI | Ticket Triage (TypeSafe) | Console routing tiket dengan `Gravicode.TypeSafeSdk`, jalan offline via simulator |

Template disimpan di `TypeSafeAppGen/Templates/<id>/` dengan akhiran `.txt` agar tidak ikut dikompilasi oleh AppGen. `template.json` berisi metadata; placeholder `__ProjectName__` diganti nama proyek yang sudah dijadikan identifier C#. Menambah template cukup dengan menambah folder baru.

![Template Terrain Flyover dijalankan dengan F5](images/appgen-run-template.png)

## Build, Run, Deploy

- **Build** (Ctrl+Shift+B) menjalankan `dotnet build` pada `.slnx`/`.sln` di root, atau `.csproj` terdangkal.
- **Run** (F5) menjalankan `dotnet run` pada proyek Exe/WinExe/Web pertama; **Stop** (Shift+F5) menghentikan seluruh process tree.
- **Deploy** menjalankan `dotnet publish -c Release` dengan pilihan target (framework-dependent, Windows, Linux, macOS), self-contained, single file, dan folder output.
- Semua file yang belum disimpan otomatis disimpan sebelum build. Bila aplikasi sedang di-Run saat Jack meminta build, aplikasi dihentikan dulu karena exe yang terkunci membuat build gagal.
- Output di-stream per batch dan dibatasi ukurannya supaya build panjang tetap ringan.

![Panel Problems](images/appgen-problems.png)

## Shortcut

| Tombol | Aksi |
| --- | --- |
| Ctrl+L / Ctrl+Enter | Fokus ke Jack / kirim |
| Ctrl+I | Tanya Jack tentang file ini |
| Ctrl+Shift+N / Ctrl+N | Proyek baru / file baru |
| Ctrl+K / Ctrl+O | Buka folder / file |
| Ctrl+S / Ctrl+Shift+S | Simpan / simpan semua |
| Ctrl+W, Ctrl+Tab | Tutup tab, pindah tab |
| Ctrl+G | Go to line (`42` atau `42:8`) |
| Ctrl+F | Find/Replace |
| Shift+Alt+F | Format code (C# via Roslyn, JSON, XML/XAML/csproj) |
| Ctrl+Shift+B, F5, Shift+F5 | Build, Run, Stop |
| Ctrl+B, Ctrl+J, Ctrl+Alt+B | Toggle Explorer, panel Output, panel Jack |
| Alt+Z, Ctrl+= / Ctrl+- | Word wrap, zoom |

## Struktur kode

```text
TypeSafeAppGen/
  Config/AppConfig.cs        konfigurasi + migrasi app.config.json
  Ai/JackAgent.cs            thread chat, streaming, filter tool, pesan error
  Ai/LlmFactory.cs           konektor per penyedia dan execution settings
  Ai/Plugins/                kernel functions (workspace, project, web, math, time, typesafe)
  Workspace/                 path guard, runner proses, parser diagnostic, formatter, template
  Editor/                    tab editor, grammar TextMate, tema Patina
  Views/                     MainWindow (partial: Project, Build, Jack, Start) dan dialog
  Templates/                 12 template + kerangka _base
```

Logika non-UI diuji di `TypeSafeAppGen.Tests` (77 test, offline): path guard, parser diagnostic, evaluator ekspresi, formatter, migrasi konfigurasi, scaffolding setiap template, dan kernel functions dengan host palsu.

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil.
