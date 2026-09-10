using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;
using PollenRobotics.Net.Wizard.Core.Templates;
using Spectre.Console;

namespace PollenRobotics.Net.Cli.Commands;

/// <summary>Lists the project templates.</summary>
internal static class TemplatesCommand
{
    public static int Run(string[] args)
    {
        string filter = args.FirstOrDefault(a => !a.StartsWith('-')) ?? string.Empty;
        IReadOnlyList<RobotTemplate> templates = TemplateCatalog.Search(filter);

        if (templates.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Nothing matches '{Markup.Escape(filter)}'.[/] Run without a filter to see all {TemplateCatalog.Count}.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("id");
        table.AddColumn("name");
        table.AddColumn("robot");
        table.AddColumn(new TableColumn("level").Centered());
        table.AddColumn("what it does");

        foreach (RobotTemplate template in templates)
        {
            table.AddRow(
                $"[green]{template.Id}[/]",
                Markup.Escape(template.Name),
                template.Robot.ToString(),
                new string('*', template.Difficulty).PadRight(3, '.'),
                $"[dim]{Markup.Escape(template.Description)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{templates.Count} template(s). Scaffold one with: pollen new <id> <name>[/]");
        return 0;
    }
}

/// <summary>Scaffolds a project from a template.</summary>
internal static class NewCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string[] positional = [.. args.Where(a => !a.StartsWith('-'))];

        if (positional.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] pollen new <template-id|blank> <project-name> [[directory]]");
            AnsiConsole.MarkupLine("[dim]Run 'pollen templates' to see the ids.[/]");
            return 2;
        }

        string templateId = positional[0];
        string projectName = positional[1];
        string directory = positional.Length > 2 ? positional[2] : Directory.GetCurrentDirectory();

        try
        {
            WizardProject project;

            if (templateId.Equals("blank", StringComparison.OrdinalIgnoreCase))
            {
                string robotName = ReadOption(args, "--robot") ?? "reachy-mini";

                if (!RobotNames.TryParse(robotName, out RobotKind robot))
                {
                    AnsiConsole.MarkupLine($"[red]Unknown robot '{Markup.Escape(robotName)}'.[/]");
                    return 2;
                }

                project = await TemplateCatalog.ScaffoldBlankAsync(projectName, directory, robot);
            }
            else
            {
                RobotTemplate? template = TemplateCatalog.Find(templateId);

                if (template is null)
                {
                    AnsiConsole.MarkupLine($"[red]No template with id '{Markup.Escape(templateId)}'.[/]");

                    // Suggest the near misses rather than dumping the whole catalogue.
                    IReadOnlyList<RobotTemplate> near = TemplateCatalog.Search(templateId.Split('.')[0]);

                    if (near.Count > 0)
                    {
                        AnsiConsole.MarkupLine("[dim]Did you mean:[/]");

                        foreach (RobotTemplate candidate in near.Take(5))
                        {
                            AnsiConsole.MarkupLine($"  [green]{candidate.Id}[/]  {Markup.Escape(candidate.Name)}");
                        }
                    }

                    return 2;
                }

                project = await TemplateCatalog.ScaffoldAsync(template, projectName, directory);
            }

            AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(project.Directory)}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Next:");
            AnsiConsole.MarkupLine($"  cd {Markup.Escape(projectName)}");
            AnsiConsole.MarkupLine(project.Robot == RobotKind.Reachy2
                ? "  dotnet run -- <robot-host>"
                : "  dotnet run -- --sim");

            return 0;
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
