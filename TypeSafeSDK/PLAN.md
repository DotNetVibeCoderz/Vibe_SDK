# Roadmap

## Selesai
- [x] Core client async, options, typed errors, logging hook
- [x] Choice abstraction dan simulator CI-safe
- [x] CLI model test dan schema validation
- [x] App Generator dengan konfigurasi provider/model/endpoint
- [x] Unit test SDK, simulator, environment configuration, dan HTTP contract
- [x] Dokumentasi bilingual dan struktur solution .NET 10
- [x] Fondasi sample console, API, Blazor Server, Avalonia, game/simulator templates
- [x] Adapter LLM Semantic Kernel untuk OpenAI, Claude, Gemini, dan Ollama
- [x] VS Code extension skeleton (syntax highlighting dan snippets)
- [x] Jev Gallery: katalog Avalonia lima belas use case lintas domain
- [x] Kesejajaran penuh dengan Python SDK: 12 exception bertipe, request id, Retry-After,
      retry level client, timeout, header kustom, extra body, TypeSafeConstants, ListModelsResponse
- [x] Simulator generik untuk choice, noul, dan score dengan bobot IDF dan penanganan negasi
- [x] Verifikasi kontrak terhadap API live (models, classify, noul, score, multi-question)
- [x] Publish NuGet sebagai `Gravicode.TypeSafeSdk` 1.1.0

## Berikutnya
- [ ] Sambungkan Jack di TypeSafeAppGen ke provider LLM sungguhan — `SendAsync` masih membangun
      `Kernel` kosong dan mengembalikan balasan statis meski konfigurasi provider sudah tersimpan
- [ ] Lengkapi fitur TypeSafeAppGen sesuai `requirements.md`: code explorer, go to line,
      format code, build/run/deploy, dan dialog template proyek
- [ ] Batching dan rate limiting di lapisan aplikasi untuk pemrosesan volume besar
- [ ] Workflow GitHub Actions untuk build, test, dan publish paket
