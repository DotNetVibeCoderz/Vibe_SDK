using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PollenRobotics.Net.Core.Diagnostics;

namespace PollenRobotics.Net.Ui.Controls;

/// <summary>One row in the log panel, already shaped for binding.</summary>
/// <param name="Time">Formatted timestamp.</param>
/// <param name="Source">Subsystem name.</param>
/// <param name="Message">The text.</param>
/// <param name="Foreground">Colour for the message.</param>
/// <param name="Accent">Colour for the source column.</param>
public readonly record struct LogRow(string Time, string Source, string Message, IBrush Foreground, IBrush Accent);

/// <summary>
/// A live view of a <see cref="RobotLogSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// Entries arrive on whichever thread produced them - a transport receive loop, the simulation
/// engine's timer, a build process's output reader - so every append is marshalled to the UI thread
/// here rather than being every caller's problem.
/// </para>
/// <para>
/// The view is capped independently of the sink. At 50 Hz a chatty subsystem can produce lines
/// faster than they can be laid out, and an unbounded ItemsControl is a memory leak with a
/// scrollbar.
/// </para>
/// </remarks>
public partial class LogPanel : UserControl
{
    private const int MaxRows = 500;

    /// <summary>The sink to display.</summary>
    public static readonly StyledProperty<RobotLogSink?> SinkProperty =
        AvaloniaProperty.Register<LogPanel, RobotLogSink?>(nameof(Sink));

    /// <summary>Hide entries below this level.</summary>
    public static readonly StyledProperty<RobotLogLevel> MinimumLevelProperty =
        AvaloniaProperty.Register<LogPanel, RobotLogLevel>(nameof(MinimumLevel), RobotLogLevel.Debug);

    private readonly ObservableCollection<LogRow> _rows = [];
    private RobotLogSink? _subscribed;

    /// <inheritdoc cref="SinkProperty" />
    public RobotLogSink? Sink { get => GetValue(SinkProperty); set => SetValue(SinkProperty, value); }

    /// <inheritdoc cref="MinimumLevelProperty" />
    public RobotLogLevel MinimumLevel { get => GetValue(MinimumLevelProperty); set => SetValue(MinimumLevelProperty, value); }

    /// <summary>Creates the panel.</summary>
    public LogPanel()
    {
        InitializeComponent();
        this.FindControl<ItemsControl>("Entries")!.ItemsSource = _rows;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SinkProperty)
        {
            Resubscribe(change.GetNewValue<RobotLogSink?>());
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        // Without this the sink keeps a reference to a panel that is no longer on screen, and every
        // log line marshals to a dispatcher for a control nobody can see.
        Resubscribe(null);
        base.OnDetachedFromLogicalTree(e);
    }

    private void Resubscribe(RobotLogSink? sink)
    {
        if (_subscribed is not null)
        {
            _subscribed.EntryWritten -= OnEntryWritten;
        }

        _subscribed = sink;
        _rows.Clear();

        if (sink is null)
        {
            return;
        }

        foreach (RobotLogEntry entry in sink.Snapshot())
        {
            Append(entry);
        }

        sink.EntryWritten += OnEntryWritten;
        UpdateCount();

    }

    private void OnEntryWritten(RobotLogEntry entry)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Append(entry);
            return;
        }

        // Post rather than InvokeAsync: a transport logging at 50 Hz must never block on the UI
        // thread being free, or the control loop inherits the UI's frame time.
        Dispatcher.UIThread.Post(() => Append(entry), DispatcherPriority.Background);
    }

    private void Append(RobotLogEntry entry)
    {
        if (entry.Level < MinimumLevel)
        {
            return;
        }

        _rows.Add(new LogRow(
            entry.Timestamp.ToString("HH:mm:ss.fff"),
            Truncate(entry.Source, 12),
            entry.Message,
            Resolve(entry.Level switch
            {
                RobotLogLevel.Error => "SignalBrush",
                RobotLogLevel.Warning => "AccentBrush",
                RobotLogLevel.Debug => "TextFaintBrush",
                _ => "TextBrush",
            }),
            Resolve("TextMutedBrush")));

        while (_rows.Count > MaxRows)
        {
            _rows.RemoveAt(0);
        }

        UpdateCount();

        if (this.FindControl<ToggleButton>("AutoScrollToggle") is { IsChecked: true } &&
            this.FindControl<ScrollViewer>("Scroller") is { } scroller)
        {
            scroller.ScrollToEnd();
        }
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        _rows.Clear();
        Sink?.Clear();
        UpdateCount();
    }

    // The count is pushed rather than bound. It is derived from the row list, not from a styled
    // property, and re-raising a change notification for Sink to refresh it - which is what this
    // did first - claims something changed that did not.
    private void UpdateCount()
    {
        if (this.FindControl<TextBlock>("CountLabel") is { } label)
        {
            label.Text = _rows.Count == 0 ? "empty" : $"{_rows.Count} line{(_rows.Count == 1 ? string.Empty : "s")}";
        }
    }

    private IBrush Resolve(string key) =>
        this.TryFindResource(key, out object? value) && value is IBrush brush ? brush : Brushes.Gray;

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
