using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;

namespace DepthAI.Wizard.Ai;

/// <summary>
/// Konektor Semantic Kernel untuk Anthropic Messages API.
/// </summary>
/// <remarks>
/// Ditulis sendiri karena Semantic Kernel belum memaketkan konektor Anthropic resmi.
/// Mendukung teks, lampiran gambar, streaming, dan pemanggilan tool otomatis yang
/// dipetakan dari kernel function.
/// </remarks>
public sealed class AnthropicChatCompletionService : IChatCompletionService, IDisposable
{
    private const string ApiVersion = "2023-06-01";
    private const int MaxToolIterations = 5;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _model;

    public AnthropicChatCompletionService(
        string apiKey,
        string model,
        string? endpoint = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _model = model;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();

        _http.BaseAddress ??= new Uri(endpoint ?? "https://api.anthropic.com/");
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>
    {
        [AIServiceExtensions.ModelIdKey] = "anthropic",
    };

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);

        var (system, messages) = Convert(chatHistory);
        var tools = BuildTools(kernel);

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var response = await SendAsync(system, messages, tools, executionSettings, stream: false, cancellationToken);

            var content = response["content"]?.AsArray() ?? [];
            var text = string.Concat(content
                .Where(block => block?["type"]?.GetValue<string>() == "text")
                .Select(block => block!["text"]!.GetValue<string>()));

            var toolUses = content
                .Where(block => block?["type"]?.GetValue<string>() == "tool_use")
                .ToList();

            if (toolUses.Count == 0 || kernel is null)
            {
                return [new ChatMessageContent(AuthorRole.Assistant, text)];
            }

            // Balasan asisten harus dimasukkan ulang apa adanya sebelum hasil tool,
            // karena API mencocokkan tool_result dengan tool_use lewat id di dalamnya.
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content.DeepClone(),
            });

            var results = new JsonArray();
            foreach (var toolUse in toolUses)
            {
                results.Add(await InvokeToolAsync(kernel, toolUse!, cancellationToken));
            }

            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }

        throw new InvalidOperationException(
            $"Asisten masih meminta tool setelah {MaxToolIterations} putaran. "
            + "Kemungkinan ada tool yang gagal berulang kali dan menyebabkan loop.");
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);

        var (system, messages) = Convert(chatHistory);

        // Streaming dipakai hanya untuk jawaban teks murni. Saat tool aktif, jalur
        // non-streaming yang menangani putaran tool, lalu hasilnya dipancarkan sekaligus.
        var tools = BuildTools(kernel);
        if (tools is { Count: > 0 })
        {
            var completed = await GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
            foreach (var message in completed)
            {
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, message.Content);
            }

            yield break;
        }

        using var request = BuildRequest(system, messages, null, executionSettings, stream: true);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Dibaca sampai ReadLineAsync mengembalikan null, bukan lewat EndOfStream:
        // properti itu memblokir thread untuk mengintip stream, yang mematikan
        // manfaat streaming justru pada jalur yang paling butuh responsif.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload is "[DONE]")
            {
                break;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payload);
            }
            catch (JsonException)
            {
                continue;
            }

            // Hanya delta teks yang menarik; event lain menggambarkan siklus hidup blok.
            if (node?["type"]?.GetValue<string>() == "content_block_delta"
                && node["delta"]?["text"]?.GetValue<string>() is { Length: > 0 } text)
            {
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, text);
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// Memetakan riwayat SK ke bentuk Anthropic. Pesan system dipisahkan karena
    /// Anthropic menaruhnya di field tersendiri, bukan sebagai salah satu message.
    /// </summary>
    private static (string? System, JsonArray Messages) Convert(ChatHistory history)
    {
        var systemParts = new List<string>();
        var messages = new JsonArray();

        foreach (var message in history)
        {
            if (message.Role == AuthorRole.System)
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    systemParts.Add(message.Content);
                }

                continue;
            }

            var content = new JsonArray();

            foreach (var item in message.Items)
            {
                switch (item)
                {
                    case TextContent { Text.Length: > 0 } text:
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                        break;

                    case ImageContent image when image.Data is { Length: > 0 }:
                        content.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = image.MimeType ?? "image/png",
                                ["data"] = System.Convert.ToBase64String(image.Data.Value.Span),
                            },
                        });
                        break;

                    case ImageContent { Uri: not null } image:
                        content.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "url",
                                ["url"] = image.Uri.ToString(),
                            },
                        });
                        break;
                }
            }

            if (content.Count == 0 && !string.IsNullOrWhiteSpace(message.Content))
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
            }

            if (content.Count == 0)
            {
                continue;
            }

            messages.Add(new JsonObject
            {
                ["role"] = message.Role == AuthorRole.Assistant ? "assistant" : "user",
                ["content"] = content,
            });
        }

        return (systemParts.Count > 0 ? string.Join("\n\n", systemParts) : null, messages);
    }

    /// <summary>Menerjemahkan kernel function menjadi definisi tool Anthropic.</summary>
    private static JsonArray? BuildTools(Kernel? kernel)
    {
        if (kernel is null)
        {
            return null;
        }

        var tools = new JsonArray();

        foreach (var plugin in kernel.Plugins)
        {
            foreach (var function in plugin)
            {
                var properties = new JsonObject();
                var required = new JsonArray();

                foreach (var parameter in function.Metadata.Parameters)
                {
                    properties[parameter.Name] = new JsonObject
                    {
                        ["type"] = MapType(parameter.ParameterType),
                        ["description"] = parameter.Description ?? string.Empty,
                    };

                    if (parameter.IsRequired)
                    {
                        required.Add(parameter.Name);
                    }
                }

                tools.Add(new JsonObject
                {
                    // Nama dipisah garis bawah supaya tetap unik antar plugin dan
                    // bisa dipecah lagi saat hasil tool dikembalikan.
                    ["name"] = $"{plugin.Name}_{function.Name}",
                    ["description"] = function.Description ?? string.Empty,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required,
                    },
                });
            }
        }

        return tools.Count > 0 ? tools : null;
    }

    private static string MapType(Type? type) => type is null
        ? "string"
        : Type.GetTypeCode(Nullable.GetUnderlyingType(type) ?? type) switch
        {
            TypeCode.Boolean => "boolean",
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 => "integer",
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "number",
            _ => "string",
        };

    private static async Task<JsonObject> InvokeToolAsync(
        Kernel kernel,
        JsonNode toolUse,
        CancellationToken cancellationToken)
    {
        var id = toolUse["id"]?.GetValue<string>() ?? string.Empty;
        var name = toolUse["name"]?.GetValue<string>() ?? string.Empty;

        var result = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = id,
        };

        try
        {
            var separator = name.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Nama tool '{name}' tidak sesuai format plugin_fungsi.");
            }

            var function = kernel.Plugins.GetFunction(name[..separator], name[(separator + 1)..]);

            var arguments = new KernelArguments();
            if (toolUse["input"]?.AsObject() is { } input)
            {
                foreach (var (key, value) in input)
                {
                    arguments[key] = value?.GetValueKind() switch
                    {
                        JsonValueKind.Number => value.GetValue<double>(),
                        JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
                        _ => value?.ToString(),
                    };
                }
            }

            var invocation = await function.InvokeAsync(kernel, arguments, cancellationToken);
            result["content"] = invocation.GetValue<string>() ?? invocation.ToString();
        }
        catch (Exception ex)
        {
            // Kegagalan tool dikembalikan ke model, bukan dilempar: model sering bisa
            // memperbaiki argumennya sendiri dan mencoba lagi.
            result["content"] = $"Tool gagal: {ex.Message}";
            result["is_error"] = true;
        }

        return result;
    }

    private async Task<JsonNode> SendAsync(
        string? system,
        JsonArray messages,
        JsonArray? tools,
        PromptExecutionSettings? executionSettings,
        bool stream,
        CancellationToken cancellationToken)
    {
        using var request = BuildRequest(system, messages, tools, executionSettings, stream);
        using var response = await _http.SendAsync(request, cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken)
            ?? throw new InvalidOperationException("Anthropic mengembalikan body kosong.");
    }

    private HttpRequestMessage BuildRequest(
        string? system,
        JsonArray messages,
        JsonArray? tools,
        PromptExecutionSettings? executionSettings,
        bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = ReadSetting(executionSettings, "max_tokens", 4096),
            ["messages"] = messages.DeepClone(),
            ["stream"] = stream,
        };

        if (!string.IsNullOrWhiteSpace(system))
        {
            body["system"] = system;
        }

        if (ReadSetting(executionSettings, "temperature", double.NaN) is var temperature && !double.IsNaN(temperature))
        {
            body["temperature"] = temperature;
        }

        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
        }

        return new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = JsonContent.Create(body),
        };
    }

    private static T ReadSetting<T>(PromptExecutionSettings? settings, string key, T fallback)
    {
        if (settings?.ExtensionData is null || !settings.ExtensionData.TryGetValue(key, out var value))
        {
            return fallback;
        }

        try
        {
            return (T)System.Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException)
        {
            return fallback;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Pesan error Anthropic berisi alasan yang bisa ditindaklanjuti (kunci salah,
        // kuota habis, model tidak dikenal), jadi diteruskan apa adanya ke pengguna.
        throw new HttpRequestException(
            $"Anthropic API mengembalikan {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }
}
