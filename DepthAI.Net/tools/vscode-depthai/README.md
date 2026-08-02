# DepthAI.Net — VS Code Extension

Snippet, validasi pipeline, dan integrasi perangkat OAK untuk [DepthAI.Net](../../README.md).

*Snippets, pipeline validation, and OAK device integration for DepthAI.Net.*

## Yang disediakan / What you get

### Snippet C#

Ketik prefix lalu Tab. Semua snippet memakai API DepthAI.Net yang sebenarnya.

| Prefix | Isi |
| --- | --- |
| `dai-open` | Membuka perangkat OAK |
| `dai-pipeline-rgb` | Pipeline kamera warna |
| `dai-pipeline-depth` | Stereo depth yang diselaraskan ke kamera warna |
| `dai-pipeline-detect` | Detection network |
| `dai-pipeline-spatial` | Deteksi objek dengan koordinat 3D |
| `dai-sub-image` | Berlangganan stream gambar |
| `dai-sub-detect` | Berlangganan hasil deteksi |
| `dai-sub-depth` | Berlangganan kedalaman dan membaca jarak |
| `dai-clone` | Menyalin frame agar hidup lebih lama dari callback |
| `dai-model-placeholder` | Model metadata-saja untuk pengembangan tanpa `.blob` |
| `dai-await-foreach` | Mengonsumsi stream sebagai `IAsyncEnumerable` |
| `dai-watch` | Memantau hotplug perangkat |
| `dai-overlay` | Menggambar kotak deteksi di atas frame |

### Berkas `*.pipeline.json`

- Validasi skema penuh dengan pesan yang bisa ditindaklanjuti.
- Pelengkapan otomatis untuk tipe node, nama properti, dan nilai enum.
- Pewarnaan khusus untuk path port (`rgb.preview`) dan tipe node.

### Perintah

Buka Command Palette lalu ketik "DepthAI":

| Perintah | Fungsi |
| --- | --- |
| `DepthAI: Daftar perangkat OAK` | Memindai kamera yang terhubung |
| `DepthAI: Validasi pipeline ini` | Memeriksa pipeline terhadap kemampuan perangkat |
| `DepthAI: Jalankan pipeline ini di perangkat` | Menjalankan dan melaporkan throughput tiap stream |
| `DepthAI: Buat pipeline dari preset` | Membuat berkas pipeline baru |

### Konfigurasi debug

Extension menyumbang dua snippet `launch.json`: satu untuk debug biasa, satu yang
memaksa mode simulasi sehingga bisa dijalankan tanpa kamera terpasang.

## Prasyarat / Requirements

CLI DepthAI harus terpasang:

```bash
dotnet tool install -g DepthAI.Net.Cli
```

Bila CLI dipasang di lokasi lain, atur `depthai.cliPath` pada Settings.

## Memasang dari sumber / Installing from source

Extension ini JavaScript polos, jadi tidak perlu dikompilasi:

```bash
# Salin folder ini ke direktori extension VS Code
cp -r tools/vscode-depthai ~/.vscode/extensions/vscode-depthai-dotnet-0.1.0
```

Pada Windows, target folder-nya `%USERPROFILE%\.vscode\extensions\`.

Untuk memaketkan sebagai `.vsix`:

```bash
npm install -g @vscode/vsce
cd tools/vscode-depthai
vsce package
```

---

Dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.
