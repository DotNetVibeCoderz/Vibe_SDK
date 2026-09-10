using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.MicroDuck;
using PollenRobotics.Net.ReachyMini;
using PollenRobotics.Net.Simulation;
using PollenRobotics.Net.Simulation.Robots;
using PollenRobotics.Net.Ui.Infrastructure;

namespace PollenRobotics.Net.Simulator.Views;

/// <summary>One joint's arc in the status column.</summary>
public sealed class JointView(string label, double minimum, double maximum) : ObservableObject
{
    private double _value;

    /// <summary>Joint name.</summary>
    public string Label { get; } = label;

    /// <summary>Lower limit in degrees.</summary>
    public double Minimum { get; } = minimum;

    /// <summary>Upper limit in degrees.</summary>
    public double Maximum { get; } = maximum;

    /// <summary>Current value in degrees.</summary>
    public double Value { get => _value; set => SetProperty(ref _value, value); }
}

/// <summary>
/// The simulator shell: robot picker, run controls, status, joints and the log.
/// </summary>
/// <remarks>
/// The 3D lives in a web page served by <see cref="Hosting.ViewportHost"/> and shown either inside
/// this window or in the default browser. Everything else is native, because a combo box and a
/// status table are things Avalonia does better than HTML and because the controls must stay
/// responsive even if the viewport is not.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<JointView> _joints = [];
    private readonly SimulationEngine _engine = SimulatorApp.Engine;
    private readonly DispatcherTimer _statusTimer;

    private DateTimeOffset _lastArcUpdate = DateTimeOffset.MinValue;

    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        this.FindControl<ItemsControl>("JointArcs")!.ItemsSource = _joints;
        this.FindControl<Ui.Controls.LogPanel>("Log")!.Sink = _engine.Log;

        ComboBox picker = this.FindControl<ComboBox>("RobotPicker")!;
        picker.ItemsSource = RobotCatalog.All.Select(d => d.DisplayName).ToList();
        picker.SelectedIndex = 0;

        _engine.FrameProduced += OnFrameProduced;
        _engine.RunStateChanged += OnRunStateChanged;

        // The status table reads cheap properties, so a slow timer is enough and keeps it off the
        // frame path entirely.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        UpdateThemeGlyph();
        PublishTheme();
        RebuildJoints();
        SetUpViewport();

        Opened += (_, _) => FitToScreen();
        Closing += (_, _) =>
        {
            _statusTimer.Stop();
            _engine.FrameProduced -= OnFrameProduced;
            _engine.RunStateChanged -= OnRunStateChanged;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void SetUpViewport()
    {
        EmbeddedBrowser viewport = this.FindControl<EmbeddedBrowser>("Viewport")!;
        Border fallback = this.FindControl<Border>("ViewportFallback")!;

        if (SimulatorApp.Viewport is not { } host)
        {
            viewport.IsVisible = false;
            fallback.IsVisible = true;
            this.FindControl<TextBlock>("FallbackReason")!.Text =
                "The viewport host did not start. The log has the reason; the status and joint panels still work.";
            this.FindControl<Button>("ViewportAddress")?.SetValue(IsVisibleProperty, false);
            return;
        }

        this.FindControl<SelectableTextBlock>("ViewportAddress")!.Text = host.Address.ToString();

        if (!EmbeddedBrowser.IsAvailable)
        {
            viewport.IsVisible = false;
            fallback.IsVisible = true;

            _engine.Log.Info("viewport", $"Showing the viewport in the default browser at {host.Address}.");

            // Opened once at startup so the 3D is there without anyone having to find the button.
            EmbeddedBrowser.OpenExternally(host.Address);
            return;
        }

        viewport.Source = host.Address;

        viewport.EmbeddingFailed += reason => Dispatcher.UIThread.Post(() =>
        {
            _engine.Log.Warn("viewport", $"In-window browser failed ({reason}); falling back to the default browser.");

            viewport.IsVisible = false;
            fallback.IsVisible = true;
            this.FindControl<TextBlock>("FallbackReason")!.Text =
                $"The in-window browser could not start ({reason}). The viewport runs in your default browser instead.";

            EmbeddedBrowser.OpenExternally(host.Address);
        });
    }

    private void RebuildJoints()
    {
        _joints.Clear();

        foreach (JointDescriptor joint in _engine.Robot.Description.Joints)
        {
            _joints.Add(new JointView(ShortLabel(joint.Name), joint.Lower.Degrees, joint.Upper.Degrees));
        }
    }

    private void OnFrameProduced(SimulationSnapshot snapshot)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now - _lastArcUpdate < TimeSpan.FromMilliseconds(40))
        {
            return;
        }

        _lastArcUpdate = now;

        Dispatcher.UIThread.Post(() =>
        {
            int count = Math.Min(_joints.Count, snapshot.JointPositions.Count);

            for (int i = 0; i < count; i++)
            {
                _joints[i].Value = snapshot.JointPositions[i] * 180 / Math.PI;
            }
        }, DispatcherPriority.Background);
    }

    private void OnRunStateChanged(bool running) => Dispatcher.UIThread.Post(() =>
    {
        this.FindControl<Button>("StartButton")!.IsEnabled = !running;
        this.FindControl<Button>("StopButton")!.IsEnabled = running;

        Border pill = this.FindControl<Border>("RunPill")!;
        pill.Classes.Set("simulated", running);
        this.FindControl<TextBlock>("RunPillText")!.Text = running ? "RUNNING" : "IDLE";
    });

    private void UpdateStatus()
    {
        SimulationSnapshot snapshot = _engine.LatestSnapshot;

        this.FindControl<TextBlock>("StatusRobot")!.Text = _engine.Robot.Description.DisplayName;
        this.FindControl<TextBlock>("StatusJoints")!.Text = _engine.Robot.Description.JointCount.ToString();
        this.FindControl<TextBlock>("StatusClock")!.Text = $"{snapshot.SimulationTime.TotalSeconds:0.0} s";
        this.FindControl<TextBlock>("StatusMotion")!.Text = snapshot.IsMoving ? "moving" : "still";
        this.FindControl<TextBlock>("StatusOverruns")!.Text = _engine.Overruns.ToString();
        this.FindControl<TextBlock>("RateLabel")!.Text = $"{_engine.FrequencyHz:0} Hz";
    }

    private async void OnStartClicked(object? sender, RoutedEventArgs e)
    {
        await _engine.StartAsync();

        // Bring the robot to life so the viewport shows something moving rather than a still model.
        switch (_engine.Robot)
        {
            case SimulatedReachyMini mini:
                mini.SetMotorMode(MotorMode.Enabled);
                mini.SetWobbling(true);
                _engine.Log.Info("simulator", "Reachy Mini is awake and breathing.");
                break;

            case SimulatedMicroDuck duck:
                duck.Init();
                duck.SetVelocity(DuckVelocity.Forward(0.1));
                _engine.Log.Info("simulator", "MicroDuck is initialised and walking.");
                break;

            case SimulatedReachy2 reachy:
                foreach (string part in SimulatedReachy2.PartNames)
                {
                    reachy.TurnOn(part);
                }

                _engine.Log.Info("simulator", "Reachy 2 is on. Drive it from an SDK client or the CLI.");
                break;

            default:
                break;
        }

        SetStatusBar("Running");
    }

    private async void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        await _engine.StopAsync();
        SetStatusBar("Stopped");
    }

    private void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        _engine.Reset();
        SetStatusBar("Reset to the power-on state");
    }

    private async void OnRobotChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } picker)
        {
            return;
        }

        await _engine.LoadRobotAsync(RobotCatalog.All[picker.SelectedIndex].Kind);
        RebuildJoints();
        SetStatusBar($"Loaded {_engine.Robot.Description.DisplayName}");
    }

    private void OnOpenViewportClicked(object? sender, RoutedEventArgs e)
    {
        if (SimulatorApp.Viewport is { } host)
        {
            EmbeddedBrowser.OpenExternally(host.Address);
        }
    }

    private void OnThemeClicked(object? sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeGlyph();
        PublishTheme();
    }

    /// <summary>
    /// Tells the 3D viewport which palette to use.
    /// </summary>
    /// <remarks>
    /// The viewport is a web page and cannot inherit the Avalonia theme, so the shell has to say.
    /// System theme resolves to the actual variant in force rather than the word "system", which
    /// the JavaScript side has no way to interpret.
    /// </remarks>
    private void PublishTheme() => ViewportTheme.Set(
        ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? "light" : "dark");

    private void UpdateThemeGlyph()
    {
        Button button = this.FindControl<Button>("ThemeButton")!;
        button.Content = ThemeManager.Glyph(ThemeManager.Current);
        ToolTip.SetTip(button, ThemeManager.Describe(ThemeManager.Current));
    }

    private void SetStatusBar(string text) =>
        Dispatcher.UIThread.Post(() => this.FindControl<TextBlock>("StatusBarText")!.Text = text);

    private static string ShortLabel(string jointName)
    {
        string[] parts = jointName.Split('.');
        return parts.Length <= 2 ? jointName : string.Join('.', parts[^2..]);
    }

    /// <summary>
    /// Shrinks the window to fit the screen it opens on.
    /// </summary>
    /// <remarks>
    /// The designed size assumes a desktop monitor. On a laptop the bottom of the window - the
    /// status bar, and in the wizard the entire chat composer - falls off the screen, and a window
    /// that large cannot be dragged back into view.
    /// </remarks>
    private void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        double scale = screen.Scaling;
        double availableWidth = screen.WorkingArea.Width / scale;
        double availableHeight = screen.WorkingArea.Height / scale;

        Width = Math.Min(Width, availableWidth - 40);
        Height = Math.Min(Height, availableHeight - 40);
    }

}
