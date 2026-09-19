using TypeSafeSdk;

if (args.Length == 0 || args[0] is "--help" or "-h") { Console.WriteLine("TypeSafe CLI\n  classify <text> [--live]   Classify a ticket\n  validate <a,b,c>           Validate a choice schema"); return; }
if (args[0].Equals("classify", StringComparison.OrdinalIgnoreCase))
{
    var live = args.Contains("--live"); var text = string.Join(' ', args.Skip(1).Where(x => x != "--live")); var options = TypeSafeOptions.FromEnvironment();
    if (live) { var keyFile = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY_FILE") ?? @"C:\Users\mifma\Documents\CodeSandbox\TypeSafeApiKey.txt"; try { options.ApiKey = TypeSafeApiKeyLoader.LoadFromFile(keyFile); options.Simulator = false; } catch (Exception ex) { Console.Error.WriteLine($"API key parsing failed: {ex.Message}"); Environment.ExitCode = 2; return; } } else options.Simulator = true;
    await using var client = new TypeSafeClient(options);
    try { var result = await client.SystemOneAsync(new { document = text }, Choice.Create("billing", "technical", "other")); var category = result.Choices?.GetValueOrDefault("category")?.Choice ?? "(missing category in API response)"; Console.WriteLine($"category={category}"); }
    catch (TypeSafeApiException ex) { Console.Error.WriteLine($"TypeSafe API error ({ex.StatusCode}): {ex.Message}"); Environment.ExitCode = 2; }
}
else if (args[0].Equals("validate", StringComparison.OrdinalIgnoreCase)) { var values = args.ElementAtOrDefault(1)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []; Console.WriteLine(values.Length > 0 ? $"Valid schema: {string.Join(", ", values)}" : "Schema must contain at least one choice."); }
else Console.Error.WriteLine("Unknown command. Use --help.");
