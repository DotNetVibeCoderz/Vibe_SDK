# DepthAI.Sample.DepthViewer

Warna dan kedalaman berdampingan. Arahkan kursor ke peta kedalaman untuk membaca jarak.

*Color and depth side by side. Hover the depth map to read the distance.*


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