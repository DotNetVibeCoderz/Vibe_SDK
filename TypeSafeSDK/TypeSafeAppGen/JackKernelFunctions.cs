using Microsoft.SemanticKernel;
namespace TypeSafeAppGen;
/// <summary>Kernel functions exposed to Jack for safe local project operations.</summary>
public sealed class JackKernelFunctions
{
    [KernelFunction, System.ComponentModel.Description("Read a UTF-8 source file from the current project.")]
    public async Task<string> ReadFileAsync(string path) => await File.ReadAllTextAsync(path);

    [KernelFunction, System.ComponentModel.Description("Write UTF-8 source content to a file, creating folders when necessary.")]
    public async Task WriteFileAsync(string path, string content)
    { var folder = System.IO.Path.GetDirectoryName(path); if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder); await File.WriteAllTextAsync(path, content); }

    [KernelFunction, System.ComponentModel.Description("Get current local date and time in ISO 8601 format.")]
    public string GetDateTime() => DateTimeOffset.Now.ToString("O");

    [KernelFunction, System.ComponentModel.Description("Calculate a basic arithmetic expression. Supports +, -, *, and /.")]
    public double Calculate(double left, double right, string operation) => operation switch { "+" => left + right, "-" => left - right, "*" => left * right, "/" when right != 0 => left / right, _ => throw new ArgumentException("Unsupported operation or division by zero.") };

    [KernelFunction, System.ComponentModel.Description("List built-in application templates.")]
    public string ListTemplates() => "Console, Web API, Blazor Server, Avalonia desktop, WPF desktop, 3D graphics, Animation, Game, Simulator, IoT, AI assistant, Dashboard.";
}
