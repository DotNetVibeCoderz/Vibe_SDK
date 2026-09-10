using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using PollenRobotics.Net.Core.Diagnostics;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Gallery.Demos;
using PollenRobotics.Net.Simulation;
using PollenRobotics.Net.Ui.Infrastructure;

namespace PollenRobotics.Net.Gallery.Views;

/// <summary>One joint's arc, bound to the instrument panel.</summary>
public sealed class JointView(string label, double minimum, double maximum) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private double _value;

    /// <summary>Joint name, shown under the arc.</summary>
    public string Label { get; } = label;

    /// <summary>Lower limit in degrees.</summary>
    public double Minimum { get; } = minimum;

    /// <summary>Upper limit in degrees.</summary>
    public double Maximum { get; } = maximum;

    /// <summary>Current value in degrees.</summary>
    public double Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

/// <summary>One demo in the sidebar.</summary>
/// <param name="Title">Name.</param>
/// <param name="Summary">One line.</param>
/// <param name="Select">Command that opens it.</param>
public sealed record DemoItemView(string Title, string Summary, ICommand Select);

/// <summary>A named group of demos.</summary>
/// <param name="Name">Group heading.</param>
/// <param name="Demos">Its demos.</param>
public sealed record DemoGroupView(string Name, IReadOnlyList<DemoItemView> Demos);

/// <summary>
/// The gallery window.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately code-behind rather than a view model layer. This window has one job - pick a demo,
/// run it, show the joints - and the indirection of a full MVVM stack would cost more in
/// navigability than it buys. The two visual tools that need real view models (the wizard, the
/// simulator) have them.
/// </para>
/// <para>
/// The joint arcs update from the simulation's own thread at up to 50 Hz, so every update is posted
/// to the dispatcher at background priority. Invoking synchronously would tie the simulation's tick
/// rate to how fast the UI can lay out.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly RobotLogSink _log = new();
    private readonly ObservableCollection<JointView> _joints = [];
    private readonly SimulationEngine _engine;

    private GalleryDemo? _selected;
    private CancellationTokenSource? _running;
    private DateTimeOffset _lastArcUpdate = DateTimeOffset.MinValue;

    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        _engine = new SimulationEngine(log: _log);
        _engine.FrameProduced += OnFrameProduced;

        this.FindControl<ItemsControl>("JointArcs")!.ItemsSource = _joints;
        this.FindControl<Ui.Controls.LogPanel>("Log")!.Sink = _log;

        ComboBox picker = this.FindControl<ComboBox>("RobotPicker")!;
        picker.ItemsSource = RobotCatalog.All.Select(d => d.DisplayName).ToList();
        picker.SelectedIndex = 0;

        UpdateThemeGlyph();
        RebuildJoints();
        RebuildDemoList(RobotKind.ReachyMini);

        _log.Info("gallery", $"{DemoCatalog.All.Count} demos across {RobotCatalog.All.Count} robots.");
        _log.Info("gallery", "Nothing here needs hardware. Every demo drives the built-in simulation.");

        Opened += (_, _) => FitToScreen();
        Opened += async (_, _) => await _engine.StartAsync();
        Closing += (_, _) => _running?.Cancel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void RebuildDemoList(RobotKind robot)
    {
        List<DemoGroupView> groups = [.. DemoCatalog.For(robot)
            .GroupBy(d => d.Group, StringComparer.Ordinal)
            .Select(group => new DemoGroupView(
                group.Key.ToUpperInvariant(),
                [.. group.Select(demo => new DemoItemView(
                    demo.Title,
                    demo.Summary,
                    new RelayCommand(() => SelectDemo(demo))))]))];

        this.FindControl<ItemsControl>("DemoGroups")!.ItemsSource = groups;
    }

    private void SelectDemo(GalleryDemo demo)
    {
        _selected = demo;

        this.FindControl<TextBlock>("DemoTitle")!.Text = demo.Title;
        this.FindControl<TextBlock>("DemoSummary")!.Text = demo.Summary;
        this.FindControl<SelectableTextBlock>("CodeText")!.Text = demo.Source;
        this.FindControl<Button>("RunButton")!.IsEnabled = _running is null;
        this.FindControl<Button>("CopyButton")!.IsEnabled = true;

        Border card = this.FindControl<Border>("WatchForCard")!;

        if (demo.WatchFor is { Length: > 0 } note)
        {
            this.FindControl<TextBlock>("WatchForText")!.Text = note;
            card.IsVisible = true;
        }
        else
        {
            card.IsVisible = false;
        }

        SetStatusBar($"{demo.Id} · about {demo.Duration.TotalSeconds:0}s");
    }

    private async void OnRobotChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } picker)
        {
            return;
        }

        RobotKind robot = RobotCatalog.All[picker.SelectedIndex].Kind;

        StopRunningDemo();
        await _engine.LoadRobotAsync(robot);

        RebuildJoints();
        RebuildDemoList(robot);

        _selected = null;
        this.FindControl<TextBlock>("DemoTitle")!.Text = "Pick a demo";
        this.FindControl<TextBlock>("DemoSummary")!.Text = $"{DemoCatalog.For(robot).Count} demos for this robot.";
        this.FindControl<Border>("WatchForCard")!.IsVisible = false;
        this.FindControl<Button>("RunButton")!.IsEnabled = false;
        this.FindControl<Button>("CopyButton")!.IsEnabled = false;
    }

    /// <summary>
    /// Rebuilds the arcs for whichever robot is loaded.
    /// </summary>
    /// <remarks>
    /// The joint count changes with the robot - nine, fifteen, twenty-one - so the collection is
    /// rebuilt rather than resized. Reusing views across a robot change is how an arc ends up
    /// labelled for one robot and reading another.
    /// </remarks>
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
        // 30 Hz is past what anyone can read and well under what the layout can sustain with
        // twenty-one arcs on screen. The simulation keeps its own rate regardless.
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now - _lastArcUpdate < TimeSpan.FromMilliseconds(33))
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

            this.FindControl<TextBlock>("RateLabel")!.Text =
                $"{_engine.FrequencyHz:0} Hz · t={snapshot.SimulationTime.TotalSeconds:0.0}s";
        }, DispatcherPriority.Background);
    }

    private async void OnRunClicked(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } demo || _running is not null)
        {
            return;
        }

        _running = new CancellationTokenSource();

        this.FindControl<Button>("RunButton")!.IsEnabled = false;
        this.FindControl<Button>("StopButton")!.IsEnabled = true;

        _log.Info("gallery", $"Running {demo.Id}.");
        SetStatusBar($"Running {demo.Id}");

        var context = new DemoContext(_engine, _log, SetDemoStatus);

        try
        {
            await demo.RunAsync(context, _running.Token);
            _log.Info("gallery", $"{demo.Id} finished.");
            SetDemoStatus("done");
        }
        catch (OperationCanceledException)
        {
            _log.Info("gallery", $"{demo.Id} stopped.");
            SetDemoStatus("stopped");
        }
        catch (Exception ex)
        {
            // A demo that throws must not take the window with it. The log is where an operator
            // will look, so the failure goes there rather than into a dialog they have to dismiss.
            _log.Error("gallery", $"{demo.Id} failed: {ex.Message}");
            SetDemoStatus("failed - see the log");
        }
        finally
        {
            _running?.Dispose();
            _running = null;

            this.FindControl<Button>("RunButton")!.IsEnabled = _selected is not null;
            this.FindControl<Button>("StopButton")!.IsEnabled = false;
            SetStatusBar("Ready");
        }
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e) => StopRunningDemo();

    private void StopRunningDemo()
    {
        _running?.Cancel();
        this.FindControl<Button>("StopButton")!.IsEnabled = false;
    }

    private void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        StopRunningDemo();
        _engine.Reset();
        SetDemoStatus("idle");
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } demo || Clipboard is not { } clipboard)
        {
            return;
        }

        // Avalonia 12 replaced DataObject/SetTextAsync with the DataTransfer model.
        var item = new DataTransferItem();
        item.SetText(demo.Source);

        var payload = new DataTransfer();
        payload.Add(item);

        await clipboard.SetDataAsync(payload);
        SetStatusBar("Code copied to the clipboard");
    }

    private void OnThemeClicked(object? sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeGlyph();
    }

    private void UpdateThemeGlyph()
    {
        Button button = this.FindControl<Button>("ThemeButton")!;
        button.Content = ThemeManager.Glyph(ThemeManager.Current);
        ToolTip.SetTip(button, ThemeManager.Describe(ThemeManager.Current));
    }

    private void SetDemoStatus(string status) =>
        Dispatcher.UIThread.Post(() => this.FindControl<TextBlock>("StatusText")!.Text = status);

    private void SetStatusBar(string text) =>
        Dispatcher.UIThread.Post(() => this.FindControl<TextBlock>("StatusBarText")!.Text = text);

    /// <summary>
    /// Trims a dotted joint name down to what fits under an arc.
    /// </summary>
    /// <remarks>
    /// <c>r_arm.shoulder.pitch</c> does not fit in 88 pixels, and truncating from the front loses
    /// the part that distinguishes it from <c>l_arm.shoulder.pitch</c>. Keeping the last two
    /// segments keeps the discriminating half.
    /// </remarks>
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
