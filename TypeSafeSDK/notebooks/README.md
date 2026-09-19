# .NET Notebook Examples

File `TypeSafeSdkExamples.dib` adalah notebook .NET Interactive/Polyglot Notebook.

## Menjalankan

1. Install Visual Studio Code.
2. Install extension **Polyglot Notebooks**.
3. Build SDK terlebih dahulu:

```bash
dotnet build ../TypeSafeSDK.slnx
```

4. Buka `TypeSafeSdkExamples.dib`.
5. Jalankan cell satu per satu.

Notebook berisi contoh:

- Simulator classification
- Batch ticket processing
- Domain-specific choices
- Environment configuration
- API key file parsing
- Live API call
- Logging
- Cancellation token

Notebook tidak berisi API key. Untuk live call, set:

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\Users\mifma\Documents\CodeSandbox\TypeSafeApiKey.txt'
```

Dibuat oleh Gravicode Studios, dipimpin Kang Fadhil.
