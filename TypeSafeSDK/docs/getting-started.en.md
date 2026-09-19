# Getting Started — TypeSafe .NET SDK

A quick start guide for TypeSafe SDK on .NET 10.

## Prerequisites

- .NET SDK 10
- A TypeSafe API key for live mode
- Windows, Linux, or macOS

## Build from source

```bash
git clone <repository-url>
cd TypeSafeSDK
dotnet build TypeSafeSDK.slnx
dotnet test TypeSafeSDK.slnx
```

Add the SDK project or package to your application:

```bash
dotnet add package TypeSafeSdk
```

> NuGet publishing is intentionally postponed until all applications and final tests are complete.

## First program with the simulator

The simulator needs no network connection or API key.

```csharp
using TypeSafeSdk;

await using var client = new TypeSafeClient(
    new TypeSafeOptions { Simulator = true });

var response = await client.SystemOneAsync(
    new { document = "I was charged twice. Please fix this ASAP." },
    Choice.Create("billing", "technical", "other"));

var category = response.Choices?["category"].Choice;
Console.WriteLine(category); // billing
```

## Configure the API key

Never hard-code secrets. Use environment variables:

```powershell
$env:TYPESAFE_API_KEY = "your-api-key"
$env:TYPESAFE_BASE_URL = "https://api.typesafe.ai"
$env:TYPESAFE_DEFAULT_MODEL = "jev-latest"
```

Or point the CLI to a local file parsed by the SDK:

```powershell
$env:TYPESAFE_API_KEY_FILE = "C:\secrets\TypeSafeApiKey.txt"
```

## Live mode

```bash
dotnet run --project TypeSafe.Cli -- classify "I was charged twice" --live
```

The official Python SDK contract used by this .NET SDK is:

```text
POST https://api.typesafe.ai/v1/systemone
```

Created by **Gravicode Studios**, led by Kang Fadhil.
