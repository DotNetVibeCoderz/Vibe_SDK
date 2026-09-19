# Testing

## Kesesuaian Python SDK
Panduan Python SDK resmi diverifikasi dan fitur yang relevan sudah diuji:

- System One sync-style async C#
- Multiple questions dalam satu request
- `Noul`, `Choice`, dan `Score`
- Typed answer groups: `Nouls`, `Choices`, `Scores`
- `model` dan `jev-latest`
- `GET /v1/models`
- Retry HTTP 429 dan 529
- Error HTTP 401, 422, 429, 529
- Raw question dictionary
- Unknown answer kind diabaikan agar forward-compatible
- Unknown response fields diabaikan oleh parser
- Usage token parsing
- API key file parsing
- Simulator
- CancellationToken API
- Custom HttpClient dan HTTP contract

## Menjalankan test

```bash
dotnet build TypeSafeSDK.slnx --no-restore
dotnet test TypeSafeSDK.slnx --no-restore
```

Hasil verifikasi terakhir:

```text
Passed: 21
Failed: 0
Skipped: 0
```

## Live API

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\Users\mifma\Documents\CodeSandbox\TypeSafeApiKey.txt'
dotnet run --project TypeSafe.Cli -- classify 'I was charged twice' --live
```

API key diparsing dan tidak pernah dicetak ke console.
