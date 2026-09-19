using System.Text.Json;

namespace TypeSafeSdk;

/// <summary>Membaca API key TypeSafe dari file konfigurasi lokal tanpa mencetak secret.</summary>
public static class TypeSafeApiKeyLoader
{
    private static readonly string[] KnownNames = ["apikey", "api_key", "typesafeapikey", "typesafe_api_key", "key", "token"];

    /// <summary>Mem-parsing file API key dalam format raw, KEY=VALUE, KEY: VALUE, atau JSON.</summary>
    public static string LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("API key file path is required.", nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("TypeSafe API key file was not found.", filePath);
        var content = File.ReadAllText(filePath).Trim();
        if (content.Length == 0) throw new InvalidDataException("TypeSafe API key file is empty.");

        if (content.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (IsKnownName(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                        return Validate(property.Value.GetString(), filePath);
            }
            catch (JsonException) { /* Coba format key-value di bawah. */ }
        }

        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            var colon = line.IndexOf(':');
            var separator = equals >= 0 && (colon < 0 || equals < colon) ? equals : colon;
            if (separator <= 0) continue;
            var name = line[..separator].Trim().Trim('"', '\'');
            if (!IsKnownName(name)) continue;
            var value = line[(separator + 1)..].Trim().Trim('"', '\'').TrimEnd(';').Trim();
            return Validate(value, filePath);
        }

        if (!content.Contains('\n') && !content.Contains('\r') && !content.Contains('=') && !content.Contains(':'))
            return Validate(content.Trim('"', '\''), filePath);
        throw new InvalidDataException($"No supported API key format found in '{filePath}'.");
    }

    private static bool IsKnownName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return KnownNames.Any(known => normalized.Equals(new string(known.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static string Validate(string? value, string filePath) => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidDataException($"API key value in '{filePath}' is empty.") : value.Trim();
}
