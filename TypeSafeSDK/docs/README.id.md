# Dokumentasi TypeSafe .NET SDK

- [Getting Started](getting-started.id.md)
- [Panduan penggunaan SDK](sdk-guide.id.md)
- [Testing](TESTING.md)
- [Contoh .NET Notebook](../notebooks/README.md)

## Konfigurasi
Gunakan `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, dan `TYPESAFE_DEFAULT_MODEL`. Secret tidak disimpan di repository.

## Endpoint
SDK mengikuti kontrak SDK Python resmi: `POST https://api.typesafe.ai/v1/systemone` dengan payload `state`, `model`, dan `questions`.

## Simulator
Simulator memilih `billing` untuk kata charge/billing/bayar, `technical` untuk error/bug/technical, dan `other` untuk sisanya. Cocok untuk unit test dan CI.

## CLI
`dotnet run --project TypeSafe.Cli -- classify "I was charged twice"`.

Dibuat oleh Gravicode Studios, dipimpin Kang Fadhil.
