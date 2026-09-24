# .NET Notebook Examples

[Bahasa Indonesia](README.md) · **English**

`TypeSafeSdkExamples.dib` is a .NET Interactive / Polyglot Notebook.

## Running it

1. Install Visual Studio Code.
2. Install the **Polyglot Notebooks** extension.
3. Build the SDK first:

```bash
dotnet build ../TypeSafeSDK.slnx
```

4. Open `TypeSafeSdkExamples.dib`.
5. Run the cells one at a time.

## What the notebook covers

- Simulator classification
- Batch ticket processing
- Domain-specific choices
- Environment configuration
- API key file parsing
- A live API call
- Logging
- Cancellation tokens

## API key

The notebook contains no API key. For a live call, set one first:

```powershell
$env:TYPESAFE_API_KEY_FILE = 'C:\secrets\TypeSafeApiKey.txt'
```

Created by **Gravicode Studios**, led by Kang Fadhil.
