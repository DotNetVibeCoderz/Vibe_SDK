# Development Progress

## 2026-09-19
- TypeSafe Board Games WPF diperbaiki dari fondasi menjadi game playable.
- Tic Tac Toe, Othello, dan Connect Four memiliki aturan board, legal move validation, win/draw, score, dan reset round/match.
- Othello mendukung flipping 8 arah, pass, dan perhitungan disc akhir.
- Connect Four mendukung gravity column dan deteksi empat sebaris.
- UX diperbarui: board proporsional, legal move highlight, status turn, scoreboard draw, live/simulator mode indicator, dan AI note yang informatif.
- AI TypeSafe hanya menerima daftar legal moves; local tactical fallback menjamin AI selalu membuat langkah valid ketika simulator/API gagal.
- Build solution berhasil tanpa warning/error; 21 unit test SDK lulus.

Dibuat oleh Gravicode Studios dipimpin Kang Fadhil.
