# DepthAI.Sample.PeopleCounter

Menghitung lintasan orang pada garis vertikal di tengah frame, terpisah masuk dan keluar.

*Counts people crossing a vertical line at the frame centre, split into in and out.*


> Dibuat dengan **Jack The Code Bender** — DepthAI.Net oleh Gravicode Studios, dipimpin Kang Fadhil.

## Menjalankan / Running

```bash
dotnet run
```

## Tanpa kamera OAK / Without an OAK camera

SDK otomatis memakai backend simulasi bila runtime native tidak ditemukan, jadi
aplikasi ini tetap berjalan dan menampilkan data sintetis. Untuk memaksa mode
simulasi walaupun ada hardware, pakai `DepthAiOptions.Simulated`.

The SDK falls back to a simulation backend when the native runtime is missing, so
this app still runs and shows synthetic data. To force simulation even with
hardware attached, pass `DepthAiOptions.Simulated`.