using System.Globalization;
using System.Text.Json;

namespace DepthAI.Pipelines;

/// <summary>
/// Pembaca properti node yang toleran. Nilai bisa datang sebagai objek CLR (saat
/// pipeline dibangun lewat kode) atau <see cref="JsonElement"/> (saat dimuat dari JSON),
/// jadi setiap pembacaan harus menangani keduanya.
/// </summary>
internal static class NodeProperties
{
    public static int GetInt(this IReadOnlyDictionary<string, object?> props, string key, int fallback)
        => TryGetRaw(props, key, out var raw) && TryToInt(raw, out var value) ? value : fallback;

    public static float GetFloat(this IReadOnlyDictionary<string, object?> props, string key, float fallback)
        => TryGetRaw(props, key, out var raw) && TryToFloat(raw, out var value) ? value : fallback;

    public static bool GetBool(this IReadOnlyDictionary<string, object?> props, string key, bool fallback)
    {
        if (!TryGetRaw(props, key, out var raw))
        {
            return fallback;
        }

        return raw switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }

    public static string? GetString(this IReadOnlyDictionary<string, object?> props, string key, string? fallback = null)
    {
        if (!TryGetRaw(props, key, out var raw))
        {
            return fallback;
        }

        return raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => raw.ToString(),
        };
    }

    public static TEnum GetEnum<TEnum>(this IReadOnlyDictionary<string, object?> props, string key, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!TryGetRaw(props, key, out var raw))
        {
            return fallback;
        }

        if (raw is TEnum typed)
        {
            return typed;
        }

        // Enum diserialisasi sebagai nama supaya JSON pipeline tetap terbaca manusia,
        // tapi nilai numerik dari editor tangan tetap diterima.
        var text = GetString(props, key);
        if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return TryToInt(raw, out var numeric) && Enum.IsDefined(typeof(TEnum), numeric)
            ? (TEnum)Enum.ToObject(typeof(TEnum), numeric)
            : fallback;
    }

    public static IReadOnlyList<float> GetFloatList(this IReadOnlyDictionary<string, object?> props, string key)
    {
        if (!TryGetRaw(props, key, out var raw))
        {
            return [];
        }

        switch (raw)
        {
            case IReadOnlyList<float> list:
                return list;
            case JsonElement { ValueKind: JsonValueKind.Array } element:
            {
                var values = new List<float>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    if (item.TryGetSingle(out var value))
                    {
                        values.Add(value);
                    }
                }

                return values;
            }

            default:
                return [];
        }
    }

    private static bool TryGetRaw(IReadOnlyDictionary<string, object?> props, string key, out object raw)
    {
        if (props.TryGetValue(key, out var value) && value is not null)
        {
            raw = value;
            return true;
        }

        raw = null!;
        return false;
    }

    private static bool TryToInt(object raw, out int value)
    {
        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = (int)l;
                return true;
            case double d:
                value = (int)d;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number:
                return element.TryGetInt32(out value);
            case string s:
                return int.TryParse(s, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryToFloat(object raw, out float value)
    {
        switch (raw)
        {
            case float f:
                value = f;
                return true;
            case double d:
                value = (float)d;
                return true;
            case int i:
                value = i;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number:
                return element.TryGetSingle(out value);
            case string s:
                return float.TryParse(s, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }
}
