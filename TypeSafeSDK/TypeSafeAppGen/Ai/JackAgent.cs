using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using TypeSafeAppGen.Ai.Plugins;
using TypeSafeAppGen.Config;

namespace TypeSafeAppGen.Ai;

/// <summary>Gambar yang dilampirkan pengguna pada satu pesan.</summary>
public sealed record ImageAttachment(string Name, byte[] Data, string MimeType);

/// <summary>Callback dari satu giliran Jack ke UI. Dipanggil dari thread mana pun.</summary>
public sealed class JackTurnCallbacks
{
    public Action<string> OnText { get; init; } = _ => { };
    public Action<string, string> OnToolStarted { get; init; } = (_, _) => { };
    public Action<string, bool, string> OnToolFinished { get; init; } = (_, _, _) => { };
}

/// <summary>
/// Jack — The Code Bender. Menyimpan satu thread percakapan, membangun kernel sesuai provider aktif,
/// dan membiarkan model memanggil kernel functions secara otomatis sampai tugas selesai.
/// </summary>
public sealed class JackAgent
{
    private const int MaxHistoryMessages = 80;
    private readonly IJackHost _host;
    private readonly Func<AppConfig> _config;
    private readonly HttpClient _http;
    private ChatHistory _history = [];

    public JackAgent(IJackHost host, Func<AppConfig> config)
    {
        _host = host;
        _config = config;
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public int MessageCount => _history.Count(m => m.Role == AuthorRole.User || m.Role == AuthorRole.Assistant);

    public void Clear() => _history = [];

    public async Task<string> SendAsync(string prompt, IReadOnlyList<ImageAttachment> images, JackTurnCallbacks callbacks, CancellationToken ct)
    {
        var config = _config();
        var provider = config.ActiveProvider;
        var profile = config.ActiveProfile;

        var builder = LlmFactory.CreateBuilder(provider, profile, _http, config.MaxOutputTokens);
        builder.Plugins.AddFromObject(new WorkspacePlugin(_host), "workspace");
        builder.Plugins.AddFromObject(new ProjectPlugin(_host), "project");
        builder.Plugins.AddFromObject(new WebPlugin(_http, () => _config().TavilyApiKey), "web");
        builder.Plugins.AddFromObject(new MathPlugin(), "math");
        builder.Plugins.AddFromObject(new DateTimePlugin(), "time");
        builder.Plugins.AddFromObject(new TypeSafeSdkPlugin(), "typesafe");
        builder.Services.AddSingleton<IFunctionInvocationFilter>(new ToolReporter(callbacks));
        builder.Services.AddSingleton<IAutoFunctionInvocationFilter>(new ToolRoundLimit(config.MaxToolRounds));
        var kernel = builder.Build();
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        if (_history.Count == 0) _history.AddSystemMessage(BuildSystemPrompt(config));
        var turnStart = _history.Count;
        _history.Add(await BuildUserMessageAsync(prompt, images));

        var settings = LlmFactory.CreateSettings(provider, profile.Model, config);
        var reply = new StringBuilder();
        try
        {
            await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(_history, settings, kernel, ct))
            {
                if (string.IsNullOrEmpty(chunk.Content)) continue;
                reply.Append(chunk.Content);
                callbacks.OnText(chunk.Content);
            }
        }
        catch (Exception) when (reply.Length == 0)
        {
            // Giliran gagal total: buang pesan pengguna agar percakapan tetap konsisten dan bisa dikirim ulang.
            _history.RemoveRange(turnStart, _history.Count - turnStart);
            throw;
        }

        var text = reply.ToString();
        _history.AddAssistantMessage(text.Length == 0 ? "(done)" : text);
        TrimHistory();
        return text;
    }

    private static string BuildSystemPrompt(AppConfig config) =>
        config.SystemPrompt + """


        ## How you work
        - You are embedded in a code editor. Tools named workspace-*, project-*, web-*, math-*, time-*, typesafe-* act on the user's machine.
        - Before changing a project call workspace-get_project_info, then read the files you will touch.
        - Create or replace files with workspace-write_file (complete content only), small changes with workspace-edit_file.
        - After writing C# or project files call project-build_project and fix every error it reports before you answer. Repeat until the build succeeds.
        - If no project is open and the user wants an app, call project-list_templates and project-create_project_from_template (use 'blank' when nothing fits), then build the app inside it.
        - Desktop UI: Avalonia 11.3.20 (package versions Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent 11.3.20). Web UI: Blazor Server with interactive server render mode. Target net10.0.
        - Use web-search_internet / web-scrape_web_page for APIs you are unsure about, math-calculate for arithmetic, time-get_date_time for dates.
        - For code that uses the TypeSafe SDK call typesafe-typesafe_sdk_reference first.
        - Never print secrets. Never touch files outside the project folder.
        - When you are done, reply with a short summary: what you built, which files changed, and how to run it (the Run button or F5).
        """;

    private async Task<ChatMessageContent> BuildUserMessageAsync(string prompt, IReadOnlyList<ImageAttachment> images)
    {
        // Konteks yang berubah tiap giliran ditaruh di pesan pengguna, bukan system prompt,
        // supaya prefix percakapan tetap stabil dan bisa di-cache oleh provider.
        var context = new StringBuilder("<context>\n");
        context.AppendLine($"Local time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        context.AppendLine(_host.Workspace is { } ws ? $"Open project: {ws.Name} ({ws.Root})" : "Open project: none");
        if (await _host.GetActiveEditorAsync() is { Path: { } path } editor)
            context.AppendLine($"Active editor: {path} (caret line {editor.CaretLine}{(string.IsNullOrEmpty(editor.Selection) ? "" : ", has selection")})");
        context.AppendLine("</context>");

        var items = new ChatMessageContentItemCollection { new TextContent(context + "\n" + prompt) };
        foreach (var image in images) items.Add(new ImageContent(image.Data, image.MimeType));
        return new ChatMessageContent(AuthorRole.User, items);
    }

    /// <summary>
    /// Membuang pesan terlama saat thread terlalu panjang. Potongan selalu dimulai di pesan pengguna
    /// sehingga pasangan function call/result tidak pernah terbelah.
    /// </summary>
    private void TrimHistory()
    {
        if (_history.Count <= MaxHistoryMessages) return;
        var cut = _history.Count - MaxHistoryMessages;
        while (cut < _history.Count && _history[cut].Role != AuthorRole.User) cut++;
        if (cut >= _history.Count) return;
        _history.RemoveRange(1, cut - 1);
    }

    /// <summary>Menerjemahkan kegagalan provider menjadi pesan yang menjelaskan cara memperbaikinya.</summary>
    public static string DescribeError(Exception ex, AppConfig config)
    {
        var provider = LlmFactory.DisplayName(config.ActiveProvider);
        var endpoint = config.ActiveProfile.Endpoint;
        var status = ex switch
        {
            HttpOperationException http => http.StatusCode,
            HttpRequestException request => request.StatusCode,
            _ => FindStatus(ex),
        };
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{provider} rejected the API key ({(int)status}). Check it in Settings → Models.",
            HttpStatusCode.NotFound => $"{provider} could not find model '{config.ActiveProfile.Model}' (404). Check the model or deployment name in Settings → Models.",
            HttpStatusCode.TooManyRequests => $"{provider} is rate-limiting requests (429). Wait a moment and send again.",
            HttpStatusCode.BadRequest => $"{provider} refused the request (400): {Shorten(ex.Message)}",
            { } code when (int)code >= 500 => $"{provider} had a server error ({(int)code}). Try again shortly.",
            _ when ex is LlmConfigurationException => ex.Message,
            _ when ex is HttpRequestException or TaskCanceledException { InnerException: TimeoutException } =>
                config.ActiveProvider == LlmProvider.Ollama
                    ? $"Could not reach Ollama at {endpoint}. Start it with `ollama serve`, or pick another model at the top of this panel."
                    : $"Could not reach {provider}. Check your internet connection and the endpoint in Settings → Models.",
            _ => $"{provider} error: {Shorten(ex.Message)}",
        };
    }

    private static HttpStatusCode? FindStatus(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpOperationException { StatusCode: { } a }) return a;
            if (e is HttpRequestException { StatusCode: { } b }) return b;
            // SDK Anthropic menyimpan status pada properti StatusCode di exception miliknya.
            var property = e.GetType().GetProperty("StatusCode");
            if (property?.GetValue(e) is HttpStatusCode c) return c;
            if (property?.GetValue(e) is int d) return (HttpStatusCode)d;
        }
        return null;
    }

    private static string Shorten(string text) => text.Length > 400 ? text[..400] + "…" : text;

    /// <summary>Melaporkan setiap pemanggilan function ke UI dan mengubah exception menjadi hasil yang bisa dibaca model.</summary>
    private sealed class ToolReporter(JackTurnCallbacks callbacks) : IFunctionInvocationFilter
    {
        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            var name = $"{context.Function.PluginName}.{context.Function.Name}";
            callbacks.OnToolStarted(name, DescribeArguments(context.Arguments));
            try
            {
                await next(context);
                var result = context.Result.GetValue<object>()?.ToString() ?? "";
                var failed = result.StartsWith("Error", StringComparison.Ordinal) || result.Contains("FAILED", StringComparison.Ordinal);
                callbacks.OnToolFinished(name, !failed, FirstLine(result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Model menerima pesan error dan bisa memperbaiki langkahnya, alih-alih seluruh giliran gagal.
                context.Result = new FunctionResult(context.Function, $"Error: {ex.Message}");
                callbacks.OnToolFinished(name, false, ex.Message);
            }
        }

        private static string DescribeArguments(KernelArguments arguments)
        {
            foreach (var key in new[] { "path", "query", "url", "expression", "templateId", "packageId", "pattern", "name" })
                if (arguments.TryGetValue(key, out var value) && value is not null)
                    return FirstLine(value.ToString() ?? "");
            return "";
        }

        private static string FirstLine(string text)
        {
            var line = text.Split('\n', 2)[0].Trim();
            return line.Length > 120 ? line[..120] + "…" : line;
        }
    }

    /// <summary>Menghentikan loop tool yang tidak konvergen agar biaya dan waktu tetap terkendali.</summary>
    private sealed class ToolRoundLimit(int maxRounds) : IAutoFunctionInvocationFilter
    {
        public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
        {
            if (context.RequestSequenceIndex >= maxRounds)
            {
                context.Result = new FunctionResult(context.Function, $"Tool limit of {maxRounds} rounds reached. Stop calling tools and summarize progress and next steps for the user.");
                return;
            }
            await next(context);
        }
    }
}
