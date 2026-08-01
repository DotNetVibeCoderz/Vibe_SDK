using System.Globalization;
using System.Xml.Linq;
using Unitree.Net.Ai;
using Unitree.Net.Wizard.Core.Projects;
using Unitree.Net.Wizard.Core.Tooling;

namespace Unitree.Net.Wizard.Core;

/// <summary>
/// Everything the wizard remembers between runs.
/// </summary>
/// <remarks>
/// Persisted to the application's <c>app.config</c> so the settings sit beside the executable in a
/// file an operator can open, diff and copy between machines.
/// </remarks>
public sealed class WizardSettings
{
    /// <summary>The default persona given to Jack.</summary>
    public const string DefaultSystemPrompt =
        """
        You are Jack The Code Bender, the coding assistant built into the Unitree Robot Wizard.
        You help engineers write C# applications for Unitree robots — Go2, B2, G1, H1 and R1 — using
        the Unitree.Net SDK on .NET 10.

        The one rule that matters most:
        - BEFORE you write or edit any code that touches the SDK, call describe_sdk for the area you
          are about to use. Every time. Especially when you are sure you remember the API — this SDK
          is not in your training data, so anything you recall came from a different library and will
          not compile. Being confident is not evidence.
        - BatteryStatus is StateOfChargePercent and PackVoltage, not StateOfCharge or VoltageVolts.
          NavigationResult is Arrived, not Reached. If that surprises you, that is the point.

        How you work:
        - Write complete, compiling C# that uses the real SDK types: UnitreeRobot, SportClient,
          TelemetryHub, WaypointNavigator, LowLevelController, AiWorkflowEngine.
        - Prefer the hosted pattern: Host.CreateApplicationBuilder, AddUnitreeRobot(configuration),
          then resolve UnitreeRobot and TelemetryHub from the provider.
        - get_template_code gives you a worked, compiling example. Read one before writing something
          similar; it is faster than being corrected by the compiler.
        - Explain briefly, then give the code. Engineers read the code first.
        - Read the project's own files before changing them.

        What you never do:
        - Never claim something has been tested on real hardware. Nothing in this SDK has been.
        - Never generate motion code that skips readiness checks.
        - Velocity commands require balanced standing, so call BalanceStandAsync after StandUpAsync.
        - Low-level commands do nothing until the sport service is told to release the motors.
        - Never invent SDK members. If you are unsure whether something exists, say so.

        Safety is not decoration here. Code you write may drive a 15 kg machine that can hurt someone.
        """;

    /// <summary>Which LLM provider Jack uses.</summary>
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    /// <summary>Model identifier. Empty selects the provider's default.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>API key. Ignored for Ollama.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Endpoint override, for Azure OpenAI or a non-default Ollama host.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Sampling temperature.</summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>Maximum tokens generated per reply.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>The persona and instructions Jack is given.</summary>
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    /// <summary>Whether Jack may invoke his tools without being asked each time.</summary>
    /// <remarks>
    /// On by default, unlike the robot assistant's equivalent. Jack's tools read files, search the
    /// web and write code into the open project; none of them move a robot.
    /// </remarks>
    public bool AllowAutomaticFunctionCalling { get; set; } = true;

    /// <summary>Tavily API key. Without one, web search is unavailable and says so.</summary>
    public string TavilyApiKey { get; set; } = string.Empty;

    /// <summary>Conversation turns kept as context before the oldest are dropped.</summary>
    public int MaxHistoryTurns { get; set; } = 24;

    /// <summary>Where new projects are created.</summary>
    public string WorkspacePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UnitreeProjects");

    /// <summary>Whether Run points at the simulator or a real robot.</summary>
    public RunTarget RunTarget { get; set; } = RunTarget.Simulator;

    /// <summary>Robot deployment settings.</summary>
    public DeploymentOptions Deployment { get; set; } = new();

    /// <summary>Whether the editor shows line numbers.</summary>
    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>Whether the chat panel is visible.</summary>
    public bool ChatVisible { get; set; } = true;

    /// <summary>UI theme, "dark" or "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Projects the operator has opened, most recent first.</summary>
    public List<string> RecentProjects { get; set; } = [];

    /// <summary>Builds the AI options Jack's kernel is created from.</summary>
    public AiOptions ToAiOptions() => new()
    {
        Provider = Provider,
        ModelId = ModelId,
        ApiKey = ApiKey,
        Endpoint = Endpoint,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        AllowAutomaticFunctionCalling = AllowAutomaticFunctionCalling,
        MaxHistoryTurns = MaxHistoryTurns,
    };
}

/// <summary>
/// Reads and writes <see cref="WizardSettings"/> as <c>app.config</c> app settings.
/// </summary>
/// <remarks>
/// The XML is handled directly rather than through <c>ConfigurationManager</c>, which on .NET reads
/// the config file once at startup and offers no way to see an edit made while the process is running.
/// The wizard's settings dialog needs a write to take effect immediately.
/// </remarks>
public static class WizardSettingsStore
{
    /// <summary>The path the settings are read from and written to.</summary>
    /// <remarks>
    /// The runtime renames <c>App.config</c> to <c>{assembly}.dll.config</c> on build, so that is the
    /// file that actually exists next to the executable.
    /// </remarks>
    public static string ConfigPath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Unitree.Net.Wizard") + ".dll.config");

    /// <summary>Loads the settings, falling back to defaults for anything missing or unparsable.</summary>
    public static WizardSettings Load() => LoadFrom(ConfigPath);

    /// <summary>Loads settings from a specific file.</summary>
    /// <param name="path">The config file to read.</param>
    public static WizardSettings LoadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var settings = new WizardSettings();
        ApplyEnvironmentKeys(settings);

        if (!File.Exists(path))
        {
            return settings;
        }

        Dictionary<string, string> values;

        try
        {
            values = ReadAppSettings(path);
        }
        catch (Exception exception) when (exception is IOException or System.Xml.XmlException)
        {
            // A corrupt config must not stop the application starting. Defaults are always usable, and
            // the first save rewrites the file cleanly.
            return settings;
        }

        settings.Provider = Enum.TryParse(Get(values, "Ai.Provider"), true, out LlmProvider provider)
            ? provider
            : settings.Provider;
        settings.ModelId = Get(values, "Ai.ModelId") ?? settings.ModelId;
        settings.ApiKey = Get(values, "Ai.ApiKey") ?? settings.ApiKey;
        settings.Endpoint = Get(values, "Ai.Endpoint") ?? settings.Endpoint;
        // Only honoured when the operator actually edited it. Saving always wrote the resolved prompt
        // back, so the file ended up holding a frozen copy of whatever the built-in persona was on the
        // day the application first closed — and every later improvement to it silently never arrived.
        // With the flag absent, as it is in files written before this existed, the current default wins.
        if (TryBool(Get(values, "Ai.SystemPromptIsCustom")) == true
            && Get(values, "Ai.SystemPrompt") is { } customPrompt)
        {
            settings.SystemPrompt = customPrompt;
        }
        settings.TavilyApiKey = Get(values, "Tools.TavilyApiKey") ?? settings.TavilyApiKey;
        settings.WorkspacePath = Get(values, "Workspace.Path") ?? settings.WorkspacePath;
        settings.Theme = Get(values, "Ui.Theme") ?? settings.Theme;

        settings.Temperature = TryDouble(Get(values, "Ai.Temperature")) ?? settings.Temperature;
        settings.MaxTokens = TryInt(Get(values, "Ai.MaxTokens")) ?? settings.MaxTokens;
        settings.MaxHistoryTurns = TryInt(Get(values, "Ai.MaxHistoryTurns")) ?? settings.MaxHistoryTurns;

        settings.AllowAutomaticFunctionCalling =
            TryBool(Get(values, "Ai.AllowAutomaticFunctionCalling")) ?? settings.AllowAutomaticFunctionCalling;
        settings.ShowLineNumbers = TryBool(Get(values, "Ui.ShowLineNumbers")) ?? settings.ShowLineNumbers;
        settings.ChatVisible = TryBool(Get(values, "Ui.ChatVisible")) ?? settings.ChatVisible;

        settings.RunTarget = Enum.TryParse(Get(values, "Run.Target"), true, out RunTarget target)
            ? target
            : settings.RunTarget;

        settings.Deployment.Host = Get(values, "Deploy.Host") ?? settings.Deployment.Host;
        settings.Deployment.User = Get(values, "Deploy.User") ?? settings.Deployment.User;
        settings.Deployment.Password = Get(values, "Deploy.Password") ?? settings.Deployment.Password;
        settings.Deployment.PrivateKeyPath = Get(values, "Deploy.PrivateKeyPath") ?? settings.Deployment.PrivateKeyPath;
        settings.Deployment.RemoteDirectory = Get(values, "Deploy.RemoteDirectory") ?? settings.Deployment.RemoteDirectory;
        settings.Deployment.Port = TryInt(Get(values, "Deploy.Port")) ?? settings.Deployment.Port;
        settings.Deployment.InstallService = TryBool(Get(values, "Deploy.InstallService")) ?? settings.Deployment.InstallService;

        if (Get(values, "Workspace.Recent") is { Length: > 0 } recent)
        {
            settings.RecentProjects = [.. recent.Split('|', StringSplitOptions.RemoveEmptyEntries)];
        }

        return settings;
    }

    /// <summary>Writes the settings back to <see cref="ConfigPath"/>.</summary>
    /// <param name="settings">The settings to persist.</param>
    public static void Save(WizardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool promptIsCustom = !string.Equals(
            settings.SystemPrompt, WizardSettings.DefaultSystemPrompt, StringComparison.Ordinal);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ai.Provider"] = settings.Provider.ToString(),
            ["Ai.ModelId"] = settings.ModelId,
            ["Ai.ApiKey"] = settings.ApiKey,
            ["Ai.Endpoint"] = settings.Endpoint,
            ["Ai.Temperature"] = settings.Temperature.ToString(CultureInfo.InvariantCulture),
            ["Ai.MaxTokens"] = settings.MaxTokens.ToString(CultureInfo.InvariantCulture),
            ["Ai.MaxHistoryTurns"] = settings.MaxHistoryTurns.ToString(CultureInfo.InvariantCulture),
            ["Ai.AllowAutomaticFunctionCalling"] = settings.AllowAutomaticFunctionCalling.ToString(),
            // Empty when it is still the built-in one, so the default keeps tracking the code rather
            // than being frozen at whatever it said the first time this file was written.
            ["Ai.SystemPrompt"] = promptIsCustom ? settings.SystemPrompt : string.Empty,
            ["Ai.SystemPromptIsCustom"] = promptIsCustom.ToString(),
            ["Tools.TavilyApiKey"] = settings.TavilyApiKey,
            ["Workspace.Path"] = settings.WorkspacePath,
            ["Workspace.Recent"] = string.Join('|', settings.RecentProjects.Take(10)),
            ["Run.Target"] = settings.RunTarget.ToString(),
            ["Deploy.Host"] = settings.Deployment.Host,
            ["Deploy.Port"] = settings.Deployment.Port.ToString(CultureInfo.InvariantCulture),
            ["Deploy.User"] = settings.Deployment.User,
            ["Deploy.Password"] = settings.Deployment.Password,
            ["Deploy.PrivateKeyPath"] = settings.Deployment.PrivateKeyPath,
            ["Deploy.RemoteDirectory"] = settings.Deployment.RemoteDirectory,
            ["Deploy.InstallService"] = settings.Deployment.InstallService.ToString(),
            ["Ui.Theme"] = settings.Theme,
            ["Ui.ShowLineNumbers"] = settings.ShowLineNumbers.ToString(),
            ["Ui.ChatVisible"] = settings.ChatVisible.ToString(),
        };

        var appSettings = new XElement("appSettings");

        foreach ((string key, string value) in values)
        {
            appSettings.Add(new XElement("add", new XAttribute("key", key), new XAttribute("value", value ?? string.Empty)));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XComment(" Unitree Robot Wizard settings. Edited by the Settings dialog; safe to edit by hand. "),
            new XElement("configuration", appSettings));

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        document.Save(ConfigPath);
    }

    /// <summary>
    /// Seeds API keys from the environment.
    /// </summary>
    /// <remarks>
    /// Applied before the config file so a value written in the file still wins. The point is to give
    /// operators who would rather not have a key sitting on disk somewhere to put one — an app.config
    /// gets copied between machines and committed to repositories more often than anyone intends.
    /// </remarks>
    private static void ApplyEnvironmentKeys(WizardSettings settings)
    {
        if (Environment.GetEnvironmentVariable("UNITREE_WIZARD_APIKEY") is { Length: > 0 } apiKey)
        {
            settings.ApiKey = apiKey;
        }

        if (Environment.GetEnvironmentVariable("UNITREE_WIZARD_TAVILYKEY") is { Length: > 0 } tavilyKey)
        {
            settings.TavilyApiKey = tavilyKey;
        }
    }

    private static Dictionary<string, string> ReadAppSettings(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        XElement? appSettings = XDocument.Load(path).Root?.Element("appSettings");

        foreach (XElement entry in appSettings?.Elements("add") ?? [])
        {
            if (entry.Attribute("key")?.Value is { Length: > 0 } key)
            {
                values[key] = entry.Attribute("value")?.Value ?? string.Empty;
            }
        }

        return values;
    }

    // An empty value means "not set" rather than "set to empty": that is what lets a blank entry in
    // the file fall back to the default instead of blanking the setting.
    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static double? TryDouble(string? text) =>
        double.TryParse(text, CultureInfo.InvariantCulture, out double value) ? value : null;

    private static int? TryInt(string? text) =>
        int.TryParse(text, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static bool? TryBool(string? text) =>
        bool.TryParse(text, out bool value) ? value : null;
}
