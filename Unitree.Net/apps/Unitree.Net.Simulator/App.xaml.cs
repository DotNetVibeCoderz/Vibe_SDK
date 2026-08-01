using System.Windows;
using System.Windows.Threading;

namespace Unitree.Net.Simulator;

/// <summary>
/// WPF application entry point.
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        // A Blazor component that throws would otherwise take the whole window down with a dialog that
        // says nothing useful. Surfacing the message keeps a broken render from looking like a crash.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            Describe(e.Exception),
            "Unitree.Net Simulator",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    /// <summary>
    /// Flattens an exception chain into something a person can act on.
    /// </summary>
    /// <remarks>
    /// Reflection-driven failures — dependency injection, component activation — surface as
    /// <c>TargetInvocationException</c>, whose own message says only "Exception has been thrown by the
    /// target of an invocation". The cause is always one level down, so showing only the outer message
    /// hides the entire diagnosis.
    /// </remarks>
    private static string Describe(Exception exception)
    {
        var text = new System.Text.StringBuilder();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.Append(current.GetType().Name).Append(": ").AppendLine(current.Message);
        }

        return text.ToString();
    }
}
