using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PollenRobotics.Net.Ui.Controls;

/// <summary>
/// A joint shown as the arc it can actually sweep, with a marker at where it is now.
/// </summary>
/// <remarks>
/// <para>
/// This is the signature instrument of the PollenRobotics.Net tooling, and it exists because a
/// number cannot answer the question an operator is really asking. "Shoulder pitch: -1.38 rad" tells
/// you nothing about whether the arm is about to run out of travel. An arc does: the track is the
/// joint's whole range, the marker is where it is, and the gap to the end of the track is the
/// headroom left.
/// </para>
/// <para>
/// The arc warms toward amber and then to the signal colour as the marker approaches an end stop,
/// so a joint in trouble is visible from across a room without reading anything. Colour is never
/// the only channel - <see cref="ShowValue"/> puts the number in the middle - because an instrument
/// that only speaks in hue fails the people who most need it to be clear.
/// </para>
/// </remarks>
public sealed class JointArc : Control
{
    /// <summary>The joint's lower limit, in the same unit as <see cref="Value"/>.</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<JointArc, double>(nameof(Minimum), -180);

    /// <summary>The joint's upper limit.</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<JointArc, double>(nameof(Maximum), 180);

    /// <summary>Where the joint is now.</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<JointArc, double>(nameof(Value));

    /// <summary>Where the joint has been commanded to, drawn as a ghost marker.</summary>
    public static readonly StyledProperty<double?> TargetProperty =
        AvaloniaProperty.Register<JointArc, double?>(nameof(Target));

    /// <summary>The joint's name, shown under the arc.</summary>
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<JointArc, string>(nameof(Label), string.Empty);

    /// <summary>Unit suffix for the readout.</summary>
    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<JointArc, string>(nameof(Unit), "deg");

    /// <summary>Print the value in the middle of the arc.</summary>
    public static readonly StyledProperty<bool> ShowValueProperty =
        AvaloniaProperty.Register<JointArc, bool>(nameof(ShowValue), true);

    /// <summary>Thickness of the arc stroke.</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<JointArc, double>(nameof(Thickness), 5);

    /// <summary>Colour of the empty track.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<JointArc, IBrush?>(nameof(TrackBrush));

    /// <summary>Colour of the filled arc when the joint is comfortably inside its range.</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<JointArc, IBrush?>(nameof(FillBrush));

    /// <summary>Colour used as the joint approaches a limit.</summary>
    public static readonly StyledProperty<IBrush?> WarnBrushProperty =
        AvaloniaProperty.Register<JointArc, IBrush?>(nameof(WarnBrush));

    /// <summary>Colour used at or past a limit.</summary>
    public static readonly StyledProperty<IBrush?> LimitBrushProperty =
        AvaloniaProperty.Register<JointArc, IBrush?>(nameof(LimitBrush));

    /// <summary>Colour of the label and readout.</summary>
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<JointArc, IBrush?>(nameof(TextBrush));

    /// <summary>Font used for the readout.</summary>
    public static readonly StyledProperty<FontFamily> ReadoutFontFamilyProperty =
        AvaloniaProperty.Register<JointArc, FontFamily>(nameof(ReadoutFontFamily), FontFamily.Default);

    static JointArc()
    {
        AffectsRender<JointArc>(
            MinimumProperty, MaximumProperty, ValueProperty, TargetProperty,
            LabelProperty, UnitProperty, ShowValueProperty, ThicknessProperty,
            TrackBrushProperty, FillBrushProperty, WarnBrushProperty, LimitBrushProperty, TextBrushProperty);
    }

    /// <inheritdoc cref="MinimumProperty" />
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <inheritdoc cref="MaximumProperty" />
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <inheritdoc cref="ValueProperty" />
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="TargetProperty" />
    public double? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    /// <inheritdoc cref="LabelProperty" />
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <inheritdoc cref="UnitProperty" />
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <inheritdoc cref="ShowValueProperty" />
    public bool ShowValue { get => GetValue(ShowValueProperty); set => SetValue(ShowValueProperty, value); }

    /// <inheritdoc cref="ThicknessProperty" />
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    /// <inheritdoc cref="TrackBrushProperty" />
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    /// <inheritdoc cref="FillBrushProperty" />
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    /// <inheritdoc cref="WarnBrushProperty" />
    public IBrush? WarnBrush { get => GetValue(WarnBrushProperty); set => SetValue(WarnBrushProperty, value); }

    /// <inheritdoc cref="LimitBrushProperty" />
    public IBrush? LimitBrush { get => GetValue(LimitBrushProperty); set => SetValue(LimitBrushProperty, value); }

    /// <inheritdoc cref="TextBrushProperty" />
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    /// <inheritdoc cref="ReadoutFontFamilyProperty" />
    public FontFamily ReadoutFontFamily
    {
        get => GetValue(ReadoutFontFamilyProperty);
        set => SetValue(ReadoutFontFamilyProperty, value);
    }

    /// <summary>
    /// How close to a limit the joint is, from 0 in the middle of its range to 1 at an end stop.
    /// </summary>
    public double LimitProximity
    {
        get
        {
            double span = Maximum - Minimum;

            if (span <= 0)
            {
                return 0;
            }

            double normalised = Math.Clamp((Value - Minimum) / span, 0, 1);

            // Distance from whichever end is nearer, rescaled so the middle reads as zero.
            return 1 - (Math.Min(normalised, 1 - normalised) * 2);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        // Square by default, with room under the arc for the label.
        double side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 84 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 84 : availableSize.Height);

        return new Size(Math.Max(48, side), Math.Max(48, side));
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect area = new(Bounds.Size);

        if (area.Width < 8 || area.Height < 8)
        {
            return;
        }

        bool hasLabel = !string.IsNullOrEmpty(Label);
        double labelHeight = hasLabel ? 14 : 0;

        double diameter = Math.Min(area.Width, area.Height - labelHeight);
        double radius = (diameter - Thickness) / 2;
        var centre = new Point(area.Width / 2, ((area.Height - labelHeight) / 2) + 1);

        if (radius <= 2)
        {
            return;
        }

        // The arc opens downward, leaving a 90-degree gap at the bottom. The gap is where the
        // reading goes, and it stops the two end stops from meeting and reading as a full circle -
        // which would hide exactly the thing this control exists to show.
        const double StartAngle = 135;
        const double SweepAngle = 270;

        IBrush track = TrackBrush ?? Brushes.DimGray;
        var trackPen = new Pen(track, Thickness, lineCap: PenLineCap.Round);
        context.DrawGeometry(null, trackPen, BuildArc(centre, radius, StartAngle, SweepAngle));

        double span = Maximum - Minimum;
        double normalised = span > 0 ? Math.Clamp((Value - Minimum) / span, 0, 1) : 0.5;

        IBrush fill = ProximityBrush();

        if (normalised > 0.001)
        {
            var fillPen = new Pen(fill, Thickness, lineCap: PenLineCap.Round);
            context.DrawGeometry(null, fillPen, BuildArc(centre, radius, StartAngle, SweepAngle * normalised));
        }

        // The commanded position, when it differs from the measured one. Seeing the two apart is
        // how an operator tells "still moving" from "stopped short".
        if (Target is { } target && span > 0)
        {
            double targetNormalised = Math.Clamp((target - Minimum) / span, 0, 1);

            if (Math.Abs(targetNormalised - normalised) > 0.01)
            {
                Point tip = PointOnArc(centre, radius, StartAngle + (SweepAngle * targetNormalised));
                Point inner = PointOnArc(centre, radius - (Thickness * 1.4), StartAngle + (SweepAngle * targetNormalised));
                context.DrawLine(new Pen(track, 1.5), inner, tip);
            }
        }

        // The marker sits on the arc rather than beside it, so its position is unambiguous.
        Point markerCentre = PointOnArc(centre, radius, StartAngle + (SweepAngle * normalised));
        context.DrawEllipse(fill, null, markerCentre, Thickness * 0.72, Thickness * 0.72);

        if (ShowValue && radius > 16)
        {
            var readout = new FormattedText(
                $"{Value:0.#}",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(ReadoutFontFamily, weight: FontWeight.SemiBold),
                Math.Clamp(radius * 0.52, 10, 17),
                TextBrush ?? Brushes.White);

            context.DrawText(readout, new Point(
                centre.X - (readout.Width / 2),
                centre.Y - (readout.Height / 2) - 1));

            if (radius > 26 && !string.IsNullOrEmpty(Unit))
            {
                var unit = new FormattedText(
                    Unit,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(ReadoutFontFamily),
                    9,
                    track);

                context.DrawText(unit, new Point(
                    centre.X - (unit.Width / 2),
                    centre.Y + (readout.Height / 2) - 2));
            }
        }

        if (hasLabel)
        {
            var label = new FormattedText(
                Label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(ReadoutFontFamily),
                9.5,
                track);

            // Truncated rather than wrapped: a dotted joint name that wraps onto two lines pushes
            // the arc out of alignment with its neighbours in a grid.
            if (label.Width > area.Width)
            {
                label.MaxTextWidth = area.Width;
                label.Trimming = TextTrimming.CharacterEllipsis;
            }

            context.DrawText(label, new Point(
                Math.Max(0, (area.Width - Math.Min(label.Width, area.Width)) / 2),
                area.Height - labelHeight + 1));
        }
    }

    /// <summary>
    /// Picks the arc colour from how close the joint is to an end stop.
    /// </summary>
    /// <remarks>
    /// The thresholds are deliberately late. Warning at half travel would paint most of a working
    /// robot amber and train the operator to ignore it.
    /// </remarks>
    private IBrush ProximityBrush()
    {
        double proximity = LimitProximity;

        return proximity switch
        {
            > 0.97 => LimitBrush ?? Brushes.OrangeRed,
            > 0.86 => WarnBrush ?? Brushes.Orange,
            _ => FillBrush ?? Brushes.SteelBlue,
        };
    }

    /// <summary>
    /// Builds an arc as a geometry.
    /// </summary>
    /// <remarks>
    /// <see cref="ArcSegment"/> needs to be told whether the arc is the long way round, and it gets
    /// that from <c>IsLargeArc</c> rather than working it out. Leaving it false on a 270-degree
    /// sweep silently draws the 90-degree complement instead, which looks like a joint reading the
    /// opposite of the truth.
    /// </remarks>
    private static StreamGeometry BuildArc(Point centre, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();

        using (StreamGeometryContext context = geometry.Open())
        {
            Point start = PointOnArc(centre, radius, startDegrees);
            Point end = PointOnArc(centre, radius, startDegrees + sweepDegrees);

            context.BeginFigure(start, isFilled: false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: Math.Abs(sweepDegrees) > 180,
                sweepDirection: sweepDegrees >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            context.EndFigure(isClosed: false);
        }

        return geometry;
    }

    /// <summary>
    /// A point on the arc.
    /// </summary>
    /// <remarks>
    /// Angles are measured clockwise from the positive x axis, which is what screen coordinates
    /// give once y points down. Zero degrees is to the right; 135 is down-left, where the arc
    /// starts.
    /// </remarks>
    private static Point PointOnArc(Point centre, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new Point(
            centre.X + (radius * Math.Cos(radians)),
            centre.Y + (radius * Math.Sin(radians)));
    }
}
