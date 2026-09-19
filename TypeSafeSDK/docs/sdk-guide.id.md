# Panduan Penggunaan SDK

Panduan ini mengikuti Python SDK resmi TypeSafe.

## System One dengan beberapa question

```csharp
await using var client = new TypeSafeClient(new TypeSafeOptions { Simulator = true });
var questions = new Dictionary<string, object>
{
    ["billing"] = new NoulSchema("Is this about billing?").ToApiQuestion(),
    ["tone"] = Choice.Create("calm", "angry").ToApiQuestion(),
    ["urgency"] = new ScoreSchema("How urgent is this?", "low", "medium", "high").ToApiQuestion()
};
var result = await client.SystemOneAsync("I was charged twice. Please help ASAP.", questions, "jev-latest");
```

Akses hasil berdasarkan tipe:

```csharp
var billing = result.Nouls["billing"].Noul;
var tone = result.Choices["tone"].Choice;
var urgency = result.Scores["urgency"].Score;
```

## Pemilihan model

```csharp
var options = new TypeSafeOptions
{
    ApiKey = "from-secret-manager",
    DefaultModel = "jev-latest"
};
await using var client = new TypeSafeClient(options);
```

## Daftar model

```csharp
var models = await client.Models.ListAsync();
foreach (var model in models) Console.WriteLine(model.Name);
```

## Retry

SDK melakukan retry untuk HTTP 429 dan 529 jika policy diberikan:

```csharp
var retry = new RetryPolicy(
    MaxRetries: 3,
    Delay: TimeSpan.FromMilliseconds(200),
    MaxDelay: TimeSpan.FromSeconds(2));

var result = await client.SystemOneAsync(
    state,
    questions,
    model: "jev-latest",
    retry: retry,
    cancellationToken: cancellationToken);
```

## Raw question dictionary

Question dictionary mentah berguna untuk fitur API baru:

```csharp
var questions = new Dictionary<string, object>
{
    ["billing"] = new
    {
        type = "noul",
        instructions = "About billing?",
        weight = 2 // field tambahan diteruskan ke API
    }
};
```

## Typed response

Response resmi dapat diakses strongly typed melalui `Answers`, `Nouls`, `Choices`, dan `Scores`:

```csharp
Answer answer = result.Answers["billing"];
if (answer is NoulAnswer noul) Console.WriteLine(noul.Noul);
if (answer is ChoiceAnswer choice) Console.WriteLine(choice.Choice);
if (answer is ScoreAnswer score) Console.WriteLine(score.Score);
```

## Error handling

```csharp
try { await client.SystemOneAsync(state, questions); }
catch (TypeSafeApiException ex) when (ex.StatusCode == 401) { /* credential error */ }
catch (TypeSafeApiException ex) when (ex.StatusCode == 422) { /* validation error */ }
catch (TypeSafeApiException ex) when (ex.StatusCode is 429 or 529) { /* retry/backoff */ }
```

## Environment variables

- `TYPESAFE_API_KEY`
- `TYPESAFE_BASE_URL` — default `https://api.typesafe.ai`
- `TYPESAFE_DEFAULT_MODEL` — default `jev-latest`
- `TYPESAFE_SIMULATOR=true`

Dibuat oleh Gravicode Studios, dipimpin Kang Fadhil.
