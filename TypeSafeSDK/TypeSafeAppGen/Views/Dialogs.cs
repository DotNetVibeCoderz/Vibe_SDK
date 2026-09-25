using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace TypeSafeAppGen.Views;

public enum SaveChoice { Save, Discard, Cancel }

/// <summary>Dialog kecil yang dibangun di code-behind: konfirmasi simpan, konfirmasi hapus, dan input teks.</summary>
public static class Dialogs
{
    private static Window Shell(string title, double width)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Ui.Brush("PatinaPanel"),
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        return window;
    }

    private static StackPanel Body(string heading, string message)
    {
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 18), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (message.Length > 0)
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Muted"), LineHeight = 19 });
        return body;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    public static async Task<SaveChoice> AskSaveAsync(Window owner, string fileNames)
    {
        var result = SaveChoice.Cancel;
        var window = Shell("Unsaved changes", 440);
        var save = new Button { Content = "Save" }.WithClass("primary");
        var discard = new Button { Content = "Don't save" }.WithClass("ghost");
        var cancel = new Button { Content = "Cancel" }.WithClass("ghost");
        save.Click += (_, _) => { result = SaveChoice.Save; window.Close(); };
        discard.Click += (_, _) => { result = SaveChoice.Discard; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        var body = Body("Save changes before closing?", fileNames);
        body.Children.Add(Buttons(discard, cancel, save));
        window.Content = body;
        await window.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string heading, string message, string confirmLabel)
    {
        var confirmed = false;
        var window = Shell(heading, 420);
        var confirm = new Button { Content = confirmLabel }.WithClass("primary");
        var cancel = new Button { Content = "Cancel" }.WithClass("ghost");
        confirm.Click += (_, _) => { confirmed = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        var body = Body(heading, message);
        body.Children.Add(Buttons(cancel, confirm));
        window.Content = body;
        await window.ShowDialog(owner);
        return confirmed;
    }

    /// <summary>Meminta satu baris teks; <paramref name="validate"/> mengembalikan pesan error atau null.</summary>
    public static async Task<string?> PromptAsync(Window owner, string heading, string label, string initial, string confirmLabel, Func<string, string?>? validate = null)
    {
        string? result = null;
        var window = Shell(heading, 440);
        var input = new TextBox { Text = initial, Watermark = label }.WithClass("field");
        var error = new TextBlock { Foreground = Ui.Brush("Ember"), IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var confirm = new Button { Content = confirmLabel, IsDefault = true }.WithClass("primary");
        var cancel = new Button { Content = "Cancel" }.WithClass("ghost");

        void Submit()
        {
            var value = input.Text?.Trim() ?? "";
            var problem = validate?.Invoke(value) ?? (value.Length == 0 ? "Enter a value." : null);
            if (problem is not null)
            {
                error.Text = problem;
                error.IsVisible = true;
                return;
            }
            result = value;
            window.Close();
        }

        confirm.Click += (_, _) => Submit();
        cancel.Click += (_, _) => window.Close();
        var body = Body(heading, "");
        body.Children.Add(Ui.Eyebrow(label));
        body.Children.Add(input);
        body.Children.Add(error);
        body.Children.Add(Buttons(cancel, confirm));
        window.Content = body;
        window.Opened += (_, _) =>
        {
            input.Focus();
            // Pilih nama tanpa ekstensi, seperti rename di VS Code.
            var dot = initial.LastIndexOf('.');
            input.SelectionStart = 0;
            input.SelectionEnd = dot > 0 ? dot : initial.Length;
        };
        await window.ShowDialog(owner);
        return result;
    }
}
