using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Unitree.Net.Wizard;

/// <summary>
/// WPF application entry point.
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            Describe(e.Exception),
            "Unitree Robot Wizard",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    /// <summary>
    /// Flattens an exception chain into something a person can act on.
    /// </summary>
    /// <remarks>
    /// Reflection-driven failures — dependency injection, component activation — surface as
    /// <c>TargetInvocationException</c>, whose message says only "Exception has been thrown by the
    /// target of an invocation". The cause is always one level down.
    /// </remarks>
    private static string Describe(Exception exception)
    {
        var text = new StringBuilder();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.Append(current.GetType().Name).Append(": ").AppendLine(current.Message);
        }

        return text.ToString();
    }
}
