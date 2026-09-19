# TypeSafe .NET SDK Documentation

- [Getting Started](getting-started.en.md)
- [SDK Usage Guide](sdk-guide.en.md)
- [Testing](TESTING.md)
- [.NET Notebook Examples](../notebooks/README.md)

## Configuration
Use `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL`, and `TYPESAFE_DEFAULT_MODEL`. Secrets are never stored in source control.

## Endpoint
The SDK follows the official Python SDK contract: `POST https://api.typesafe.ai/v1/systemone` with `state`, `model`, and `questions` fields.

## Simulator
The simulator returns `billing` for charge/billing/bayar terms, `technical` for error/bug/technical terms, and `other` otherwise. It is safe for unit tests and CI.

## CLI
`dotnet run --project TypeSafe.Cli -- classify "I was charged twice"`.

Created by Gravicode Studios, led by Kang Fadhil.
