using System.Text;
using Gravicode.HFNet.GraviTransformers;

namespace SiWare.Ai;

/// <summary>
/// Runs a small instruction-tuned decoder (Qwen2/Qwen2.5, Llama, Mistral, GPT-2 families) entirely on this machine with
/// HF.Net. The first use downloads the checkpoint from the Hugging Face Hub (gated models read <c>HF_TOKEN</c>).
/// </summary>
/// <remarks>
/// CPU inference in managed code is slow for anything beyond ~0.5B parameters; prefer
/// <see cref="ChatCompletionsInterpreter"/> for interactive use and this class for offline or air-gapped work.
/// The prompt uses the ChatML template that Qwen models expect.
/// </remarks>
public sealed class LocalModelInterpreter : ISpectrumInterpreter, IDisposable
{
    public const string DefaultModel = "Qwen/Qwen2.5-0.5B-Instruct";
    private readonly CausalLanguageModel model;
    private readonly GenerationSettings settings;
    private readonly SemaphoreSlim gate = new(1, 1);

    private LocalModelInterpreter(CausalLanguageModel model, int maxNewTokens)
    {
        this.model = model;
        settings = new GenerationSettings(MaxNewTokens: maxNewTokens, RepetitionPenalty: 1.1);
    }

    /// <summary>Downloads (or reuses the Hub cache for) <paramref name="repoId"/> and loads it.</summary>
    public static Task<LocalModelInterpreter> LoadAsync(string repoId = DefaultModel, int maxNewTokens = 220, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        return Task.Run(() => new LocalModelInterpreter(CausalLanguageModel.Load(repoId), maxNewTokens), cancellationToken);
    }

    /// <summary>Loads a checkpoint folder (config.json, safetensors, tokenizer.json) without network access.</summary>
    public static LocalModelInterpreter Open(string directory, int maxNewTokens = 220) => new(CausalLanguageModel.Open(directory), maxNewTokens);

    public string Model => model.Id;

    public async Task<Interpretation> InterpretAsync(string facts, string language = "en", CancellationToken cancellationToken = default)
    {
        language = InterpretationPrompt.NormalizeLanguage(language);
        var prompt = ChatMl(InterpretationPrompt.System(language), InterpretationPrompt.User(facts));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = await Task.Run(() =>
            {
                var output = new StringBuilder();
                foreach (var piece in model.Stream(prompt, settings))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Append(piece);
                    if (output.ToString().Contains("<|im_end|>", StringComparison.Ordinal)) break;
                }
                return output.ToString().Split("<|im_end|>")[0];
            }, cancellationToken).ConfigureAwait(false);
            return new(InterpretationPrompt.Clean(text), "hf-local", model.Id, language, DateTimeOffset.UtcNow);
        }
        finally { gate.Release(); }
    }

    internal static string ChatMl(string system, string user) =>
        $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n";

    public void Dispose()
    {
        model.Dispose();
        gate.Dispose();
    }
}
