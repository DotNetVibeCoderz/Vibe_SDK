using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace TypeSafeAppGen.Views;

/// <summary>Ikon garis 24×24 (gaya stroke) yang dipakai toolbar, explorer, dan panel.</summary>
public static class Icons
{
    public const string NewProject = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M12 10v6 M9 13h6";
    public const string NewFile = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M12 18v-6 M9 15h6";
    public const string Folder = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z";
    public const string File = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6";
    public const string Save = "M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z M17 21v-8H7v8 M7 3v5h8";
    public const string Format = "M4 6h16 M8 12h12 M4 18h16 M4 10l2 2-2 2";
    public const string GoToLine = "M4 9h16 M4 15h16 M10 3L8 21 M16 3l-2 18";
    public const string Build = "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z";
    public const string Run = "M7 4l13 8-13 8z";
    public const string Stop = "M6 6h12v12H6z";
    public const string Deploy = "M12 15V3 M7 8l5-5 5 5 M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4";
    public const string SidebarLeft = "M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M9 3v18";
    public const string PanelBottom = "M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M3 15h18";
    public const string SidebarRight = "M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M15 3v18";
    public const string Settings = "M4 21v-7 M4 10V3 M12 21v-9 M12 8V3 M20 21v-5 M20 12V3 M1 14h6 M9 8h6 M17 16h6";
    public const string Attach = "M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48";
    public const string Send = "M12 19V5 M5 12l7-7 7 7";
    public const string Trash = "M3 6h18 M8 6V4h8v2 M19 6l-1 14H6L5 6";
    public const string Close = "M18 6L6 18 M6 6l12 12";
    public const string Refresh = "M21 12a9 9 0 1 1-3-6.7L21 8 M21 3v5h-5";
    public const string Collapse = "M4 14h6v6 M20 10h-6V4 M14 10l7-7 M3 21l7-7";
    public const string Copy = "M9 9h11v11H9z M5 15H4V4h11v1";
    public const string Insert = "M12 3v12 M7 10l5 5 5-5 M5 21h14";
    public const string Check = "M20 6L9 17l-5-5";
    public const string Alert = "M12 8v5 M12 16.5v.5 M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z";
    public const string ChevronRight = "M9 18l6-6-6-6";
    public const string Search = "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z M21 21l-4.35-4.35";
    public const string Spark = "M12 3v4 M12 17v4 M3 12h4 M17 12h4 M5.6 5.6l2.8 2.8 M15.6 15.6l2.8 2.8 M5.6 18.4l2.8-2.8 M15.6 8.4l2.8-2.8";

    /// <summary>Tanda Jack: batang yang ditekuk 90° — tanda "bend" yang sama dengan rel di tiap balasannya.</summary>
    public const string Bend = "M5 21V11a7 7 0 0 1 7-7h8";
}

/// <summary>Helper kecil untuk membangun UI di code-behind dengan token desain dari App.axaml.</summary>
public static class Ui
{
    public static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Magenta;

    public static FontFamily Font(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) && value is FontFamily font ? font : FontFamily.Default;

    public static Path Icon(string data, double size = 16, IBrush? stroke = null, double thickness = 1.7)
    {
        var path = new Path
        {
            Data = StreamGeometry.Parse(data),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Tanpa warna eksplisit, stroke mengikuti Foreground turunan (termasuk state hover tombol induk).
        if (stroke is not null) path.Stroke = stroke;
        else path.Bind(Shape.StrokeProperty, path.GetObservable(TextElement.ForegroundProperty));
        return path;
    }

    /// <summary>Tombol ikon + label untuk toolbar; tooltip memuat shortcut.</summary>
    public static Button ToolButton(string icon, string label, string tooltip, EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick, string? extraClass = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var button = new Button { Content = content };
        content.Children.Add(Icon(icon, 15));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Classes.Add("tool");
        if (extraClass is not null) button.Classes.Add(extraClass);
        ToolTip.SetTip(button, tooltip);
        button.Click += onClick;
        return button;
    }

    public static Button IconButton(string icon, string tooltip, EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick, double size = 15)
    {
        var button = new Button();
        button.Content = Icon(icon, size);
        button.Classes.Add("icon");
        ToolTip.SetTip(button, tooltip);
        button.Click += onClick;
        return button;
    }


    public static TextBlock Eyebrow(string text) => new TextBlock { Text = text.ToUpperInvariant() }.WithClass("eyebrow");

    public static T WithClass<T>(this T control, string className) where T : StyledElement
    {
        control.Classes.Add(className);
        return control;
    }
}
