using PollenRobotics.Net.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PollenRobotics.Net.Ai;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;
using PollenRobotics.Net.Wizard.Core.Templates;

namespace PollenRobotics.Net.Wizard.Dialogs;

/// <summary>
/// Shared chrome for the wizard's dialogs.
/// </summary>
/// <remarks>
/// Built in code rather than XAML. These are five small forms that differ only in their fields, and
/// five .axaml files plus five code-behinds would be more surface to keep in step with the theme
/// than the layouts are worth.
/// </remarks>
internal abstract class WizardDialog : Window
{
    protected WizardDialog(string title, double width, double height)
    {
        Title = title;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
    }

    protected static TextBlock Eyebrow(string text) => new()
    {
        Text = text,
        Classes = { "eyebrow" },
        Margin = new Thickness(0, 10, 0, 5),
    };

    protected static TextBlock Body(string text) => new()
    {
        Text = text,
        Classes = { "muted" },
        TextWrapping = TextWrapping.Wrap,
    };

    protected static StackPanel Buttons(params Control[] controls)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
        };

        foreach (Control control in controls)
        {
            panel.Children.Add(control);
        }

        return panel;
    }

    protected static Button Primary(string text) => new() { Content = text, Classes = { "primary" }, Padding = new Thickness(18, 7) };

    protected static Button Ghost(string text) => new() { Content = text, Classes = { "ghost" }, Padding = new Thickness(14, 7) };
}

/// <summary>New Project: blank, or from one of the templates.</summary>
internal sealed class NewProjectDialog : WizardDialog
{
    public NewProjectDialog() : base("New project", 720, 640)
    {
        var nameBox = new TextBox { PlaceholderText = "MyRobotApp", Text = "MyRobotApp" };

        var locationBox = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PollenRobots"),
        };

        var browse = Ghost("Browse…");

        var robotPicker = new ComboBox
        {
            ItemsSource = RobotCatalog.All.Select(r => r.DisplayName).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var kindPicker = new ComboBox
        {
            ItemsSource = Enum.GetNames<ProjectKind>(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var search = new TextBox { PlaceholderText = "Search templates" };

        var templates = new ListBox { Height = 250 };

        // The blank project is the first entry rather than a separate mode, so New Project is one
        // list and one decision instead of two dialogs.
        void RefreshTemplates()
        {
            RobotKind robot = RobotCatalog.All[Math.Max(0, robotPicker.SelectedIndex)].Kind;

            List<object> entries = ["Blank project"];
            entries.AddRange(TemplateCatalog.Search(search.Text ?? string.Empty, robot)
                .Select(t => $"{t.Name}  —  {t.Description}"));

            templates.ItemsSource = entries;
            templates.SelectedIndex = 0;
        }

        robotPicker.SelectionChanged += (_, _) => RefreshTemplates();
        search.TextChanged += (_, _) => RefreshTemplates();
        RefreshTemplates();

        var create = Primary("Create project");
        var cancel = Ghost("Cancel");

        var error = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Margin = new Thickness(0, 8, 0, 0),
        };

        browse.Click += async (_, _) =>
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Where should the project go?", AllowMultiple = false });

            if (folders.Count > 0)
            {
                locationBox.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
            }
        };

        create.Click += async (_, _) =>
        {
            string name = (nameBox.Text ?? string.Empty).Trim();
            string location = (locationBox.Text ?? string.Empty).Trim();

            if (name.Length == 0 || location.Length == 0)
            {
                Show(error, "A project needs a name and a folder.");
                return;
            }

            RobotKind robot = RobotCatalog.All[Math.Max(0, robotPicker.SelectedIndex)].Kind;
            var kind = Enum.Parse<ProjectKind>(Enum.GetNames<ProjectKind>()[Math.Max(0, kindPicker.SelectedIndex)]);

            try
            {
                WizardProject project;

                if (templates.SelectedIndex <= 0)
                {
                    project = await TemplateCatalog.ScaffoldBlankAsync(name, location, robot, kind);
                }
                else
                {
                    IReadOnlyList<RobotTemplate> matching = TemplateCatalog.Search(search.Text ?? string.Empty, robot);
                    RobotTemplate template = matching[templates.SelectedIndex - 1];
                    project = await TemplateCatalog.ScaffoldAsync(template, name, location);
                }

                Close(project);
            }
            catch (Exception ex)
            {
                Show(error, ex.Message);
            }
        };

        cancel.Click += (_, _) => Close(null);

        Content = new ScrollViewer
        {
            Padding = new Thickness(24, 20),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "New robot project", Classes = { "display" } },
                    Body("Start from a template to get a project that already builds and runs, or from blank to get "
                       + "connection and shutdown wired up and nothing else."),

                    Eyebrow("NAME"),
                    nameBox,

                    Eyebrow("FOLDER"),
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children = { locationBox, browse },
                    },

                    Eyebrow("ROBOT"),
                    robotPicker,

                    Eyebrow("APPLICATION KIND"),
                    kindPicker,

                    Eyebrow("TEMPLATE"),
                    search,
                    templates,

                    error,
                    Buttons(cancel, create),
                },
            },
        };

        Grid.SetColumn(browse, 1);
        browse.Margin = new Thickness(8, 0, 0, 0);

        static void Show(TextBlock target, string message)
        {
            target.Text = message;
            target.IsVisible = true;
        }
    }
}

/// <summary>Go to line.</summary>
internal sealed class GoToLineDialog : WizardDialog
{
    public GoToLineDialog(int lineCount) : base("Go to line", 360, 190)
    {
        var box = new TextBox { PlaceholderText = $"1 – {lineCount}" };
        var go = Primary("Go");
        var cancel = Ghost("Cancel");

        void Commit()
        {
            if (int.TryParse(box.Text, out int line))
            {
                Close(Math.Clamp(line, 1, lineCount));
            }
            else
            {
                Close(null);
            }
        }

        go.Click += (_, _) => Commit();
        cancel.Click += (_, _) => Close(null);

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Commit();
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(22, 20),
            Children =
            {
                Body($"This file has {lineCount} lines."),
                Eyebrow("LINE"),
                box,
                Buttons(cancel, go),
            },
        };

        Opened += (_, _) => box.Focus();
    }
}

/// <summary>Deploy to a robot over SSH.</summary>
internal sealed class DeployDialog : WizardDialog
{
    public DeployDialog(string projectName) : base("Deploy to robot", 520, 380)
    {
        var hostBox = new TextBox { PlaceholderText = "pollen@reachy-mini.local" };
        var directoryBox = new TextBox { Text = "/opt/pollen-apps" };

        var ridPicker = new ComboBox
        {
            ItemsSource = new[] { "linux-arm64", "linux-x64" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var deploy = Primary("Publish and copy");
        var cancel = Ghost("Cancel");

        deploy.Click += (_, _) =>
        {
            string host = (hostBox.Text ?? string.Empty).Trim();

            if (host.Length > 0)
            {
                Close((host, (directoryBox.Text ?? "/opt/pollen-apps").Trim(),
                    (string)ridPicker.SelectedItem! ));
            }
        };

        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Children =
            {
                new TextBlock { Text = $"Deploy {projectName}", Classes = { "title" } },
                Body("Publishes self-contained for the robot's architecture and copies the result across with scp. "
                   + "Key-based SSH has to work from a terminal first."),

                Eyebrow("SSH DESTINATION"),
                hostBox,

                Eyebrow("REMOTE FOLDER"),
                directoryBox,

                Eyebrow("ARCHITECTURE"),
                ridPicker,
                Body("The robots are 64-bit ARM Linux. Publishing for the wrong architecture produces a binary that "
                   + "copies across fine and then refuses to start."),

                Buttons(cancel, deploy),
            },
        };
    }
}

/// <summary>Jack's model, persona and sampling settings.</summary>
internal sealed class JackSettingsDialog : WizardDialog
{
    public JackSettingsDialog(AiOptions current) : base("Jack settings", 620, 700)
    {
        var providerPicker = new ComboBox
        {
            ItemsSource = Enum.GetNames<AiProvider>(),
            SelectedIndex = (int)current.Provider,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var modelBox = new TextBox { Text = current.Model, PlaceholderText = AiOptions.DefaultModelFor(current.Provider) };
        var keyBox = new TextBox { Text = current.ApiKey, PasswordChar = '•' };
        var endpointBox = new TextBox { Text = current.Endpoint, PlaceholderText = "leave blank for the provider's own endpoint" };
        var tavilyBox = new TextBox { Text = current.TavilyApiKey, PasswordChar = '•' };

        var temperature = new Slider { Minimum = 0, Maximum = 2, Value = current.Temperature, TickFrequency = 0.1 };
        var temperatureLabel = new TextBlock { Classes = { "mono", "muted" }, Text = $"{current.Temperature:0.00}" };
        temperature.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                temperatureLabel.Text = $"{temperature.Value:0.00}";
            }
        };

        var maxTokens = new TextBox { Text = current.MaxTokens.ToString() };
        var functionCalling = new CheckBox { Content = "Let Jack call the SDK and web functions", IsChecked = current.EnableFunctionCalling };

        var prompt = new TextBox
        {
            Text = current.SystemPromptIsCustom ? current.SystemPrompt : AiOptions.DefaultSystemPrompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 180,
        };

        var promptIsCustom = new CheckBox
        {
            Content = "Use my persona instead of the built-in one",
            IsChecked = current.SystemPromptIsCustom,
        };

        providerPicker.SelectionChanged += (_, _) =>
            modelBox.PlaceholderText = AiOptions.DefaultModelFor((AiProvider)providerPicker.SelectedIndex);

        var save = Primary("Apply");
        var cancel = Ghost("Cancel");

        save.Click += (_, _) => Close(current with
        {
            Provider = (AiProvider)providerPicker.SelectedIndex,
            Model = (modelBox.Text ?? string.Empty).Trim(),
            ApiKey = (keyBox.Text ?? string.Empty).Trim(),
            Endpoint = (endpointBox.Text ?? string.Empty).Trim(),
            TavilyApiKey = (tavilyBox.Text ?? string.Empty).Trim(),
            Temperature = temperature.Value,
            MaxTokens = int.TryParse(maxTokens.Text, out int tokens) ? tokens : current.MaxTokens,
            EnableFunctionCalling = functionCalling.IsChecked ?? true,
            SystemPrompt = prompt.Text ?? string.Empty,
            SystemPromptIsCustom = promptIsCustom.IsChecked ?? false,
        });

        cancel.Click += (_, _) => Close(null);

        Content = new ScrollViewer
        {
            Padding = new Thickness(24, 20),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Jack The Code Bender", Classes = { "title" } },
                    Body("These settings also live in appsettings.json beside the executable, so they can be changed "
                       + "without opening the app."),

                    Eyebrow("PROVIDER"),
                    providerPicker,

                    Eyebrow("MODEL"),
                    modelBox,

                    Eyebrow("API KEY"),
                    keyBox,
                    Body("Ollama needs no key. For the others, this or the matching environment variable."),

                    Eyebrow("ENDPOINT OVERRIDE"),
                    endpointBox,

                    Eyebrow("TAVILY KEY (WEB SEARCH)"),
                    tavilyBox,
                    Body("Without a key the web search function is not registered at all, rather than offered and "
                       + "failing on every call."),

                    Eyebrow("TEMPERATURE"),
                    new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { temperature, temperatureLabel } },

                    Eyebrow("MAX TOKENS"),
                    maxTokens,

                    new StackPanel { Margin = new Thickness(0, 14, 0, 0), Children = { functionCalling } },

                    Eyebrow("PERSONA"),
                    promptIsCustom,
                    prompt,
                    Body("With the box unticked the built-in persona is used and later improvements to it still reach "
                       + "you. Tick it only if you mean to override it."),

                    Buttons(cancel, save),
                },
            },
        };

        Grid.SetColumn(temperatureLabel, 1);
        temperatureLabel.Margin = new Thickness(12, 0, 0, 0);
        temperatureLabel.VerticalAlignment = VerticalAlignment.Center;
    }
}

/// <summary>About.</summary>
internal sealed class AboutDialog : WizardDialog
{
    public AboutDialog() : base("About", 520, 440)
    {
        var close = Primary("Close");
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(28, 24),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "PollenRobotics Robot Wizard", Classes = { "display" } },
                new TextBlock { Text = $"Version {SdkInfo.Version}", Classes = { "mono", "faint" }, Margin = new Thickness(0, 2, 0, 14) },

                Body("A code editor for robot applications built on PollenRobotics.Net, the unofficial .NET SDK for "
                   + "Reachy Mini, MicroDuck and Reachy 2."),

                Eyebrow("BUILT BY"),
                new TextBlock { Text = "Gravicode Studios", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Led by Kang Fadhil", Classes = { "muted" } },

                Eyebrow("ASSISTANT"),
                new TextBlock { Text = "Jack The Code Bender", FontWeight = FontWeight.SemiBold },
                Body("Runs on Semantic Kernel with a choice of OpenAI, Anthropic, Gemini or Ollama."),

                Eyebrow("ROBOTS"),
                Body("Reachy Mini and MicroDuck run against the built-in simulator as well as real hardware. "
                   + "Reachy 2 speaks the official reachy2-sdk-api gRPC contract."),

                Buttons(close),
            },
        };
    }
}
