# TypeSafe Arcade — WPF Board Games

**Bahasa Indonesia** · [English](board-games.en.md)

`TypeSafe.BoardGames` adalah aplikasi WPF .NET 10 dengan UI arcade modern dan Jack — The Code Bender sebagai Player 2.

## Identitas pemain

| Game | Player 1 | Player 2 / Jack AI |
|---|---|---|
| Tic Tac Toe | **Cyan ✕** | **Orange ●** |
| Connect Four | **Cyan ✕** | **Orange ●** |
| Othello | **Light ●** | **Dark ●** |

Layout menampilkan legend permanen di sidebar, player cards di atas board, serta scoreboard dengan shape dan warna yang sama agar pemain tidak tertukar.

## Game playable

- **Tic Tac Toe** — 3×3, win/block tactical AI.
- **Othello** — papan 8×8, legal move highlighting, flipping disc pada delapan arah, pass, perhitungan disc akhir, dan corner priority AI.
- **Connect Four** — papan 6×7, gravitasi kolom, deteksi empat sebaris, tactical win/block AI.

## AI yang reliable

1. Semua langkah diverifikasi oleh engine lokal sebelum diterapkan.
2. Pada simulator atau bila API key kosong, Jack memakai strategi lokal deterministik.
3. Pada mode live, TypeSafe hanya boleh memilih dari daftar legal moves.
4. Jika respons API invalid atau gagal, local tactical fallback mengambil alih.

## Konfigurasi

`TypeSafe.BoardGames/app.config.json`:

```json
{
  "ApiKey": "",
  "Endpoint": "https://api.typesafe.ai",
  "DefaultModel": "jev-latest",
  "UseSimulator": true,
  "PlayerName": "You",
  "EnemyName": "Jack AI"
}
```

## Menjalankan

```bash
dotnet run --project TypeSafe.BoardGames
```

Dibuat oleh Gravicode Studios, dipimpin Kang Fadhil.
