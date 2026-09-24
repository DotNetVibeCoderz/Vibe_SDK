# Dokumentasi TypeSafe .NET

- [Panduan Awal](getting-started.id.md)
- [Panduan Penggunaan SDK](sdk-guide.id.md)
- [Kesejajaran dengan Python SDK](parity-python.id.md)
- [Jev Gallery](jev-gallery.id.md)
- [Publikasi ke NuGet](nuget.id.md)
- [Pengujian](TESTING.md)
- [Contoh .NET Notebook](../notebooks/README.md)
- [TypeSafe Board Games](board-games.id.md)

## Instalasi

```bash
dotnet add package Gravicode.TypeSafeSdk
```

Id paket diberi awalan karena ini port tidak resmi. Namespace-nya tetap `TypeSafeSdk`.

## Endpoint

SDK mengikuti kontrak Python SDK resmi: `POST https://api.typesafe.ai/v1/systemone` dengan field `state`, `model`, dan `questions`, ditambah `GET /v1/models`.

## Konfigurasi

`TypeSafeOptions.FromEnvironment()` membaca `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL`, `TYPESAFE_LOG_LEVEL`, dan `TYPESAFE_SIMULATOR`. Nama-nama itu tersedia sebagai konstanta di `TypeSafeConstants`. Secret tidak pernah disimpan di source control; `TypeSafeApiKeyLoader` membaca file key yang ditunjuk `TYPESAFE_API_KEY_FILE`.

## Simulator

`Simulator = true` menjawab pertanyaan `choice`, `noul`, dan `score` secara lokal tanpa key dan tanpa jaringan. Sifatnya leksikal, bukan semantik: kata langka pada kriteria diberi bobot lebih besar daripada kata umum, negasi dihormati, dan bila tidak ada sinyal ia mengembalikan distribusi rata. Lihat [panduan SDK](sdk-guide.id.md#simulator) untuk kapan simulator layak dipercaya.

## Aplikasi dalam repositori ini

| Proyek | Isinya |
| --- | --- |
| `TypeSafe.Cli` | `classify`, `noul`, `score`, `evaluate`, `models`, `validate`, `config` |
| `TypeSafe.JevGallery` | Katalog Avalonia berisi lima belas use case yang bisa dijalankan |
| `TypeSafe.Api` | Pembungkus Minimal API |
| `TypeSafe.Blazor` | Pengklasifikasi tiket Blazor Server |
| `TypeSafe.BoardGames` | WPF Tic Tac Toe, Othello, Connect Four |
| `TypeSafeAppGen` | Code editor Avalonia untuk asisten Jack |

```bash
dotnet run --project TypeSafe.Cli -- classify "I was charged twice"
dotnet run --project TypeSafe.JevGallery
```

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil.
