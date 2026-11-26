using HtmlMarkupRepairTool.Services;
using Spectre.Console;
using System.Text;

namespace HtmlMarkupRepairTool;

public sealed class HtmlMarkupRepairApplication
{
    private readonly CsvValidationScanner _scanner = new();
    private readonly ProductRepairService _repairService = new();

    public int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AnsiConsole.MarkupLine("[bold dodgerblue1]OpenCart Description Repair[/]");
        AnsiConsole.MarkupLine("[grey]Нормализация HTML через AngleSharp (HTML5 parser)[/]");
        AnsiConsole.WriteLine();

        var interactiveMode = args.Length == 0;
        var inputPathArgument = interactiveMode ? ReadInputPath() : args[0];
        if (string.IsNullOrWhiteSpace(inputPathArgument))
        {
            WaitForExit(interactiveMode);
            return 1;
        }

        var inputPath = Path.GetFullPath(inputPathArgument);
        if (!File.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[red]Файл не найден:[/] {Markup.Escape(inputPath)}");
            WaitForExit(interactiveMode);
            return 1;
        }

        if (interactiveMode || args.Length == 1)
        {
            try
            {
                _scanner.Scan(inputPath);
                return 0;
            }
            catch (Exception ex)
            {
                ShowError(ex);
                return 1;
            }
            finally
            {
                WaitForExit(interactiveMode);
            }
        }

        try
        {
            return _repairService.Run(inputPath, args);
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return 1;
        }
    }

    private static string? ReadInputPath()
    {
        AnsiConsole.MarkupLine("[bold]Введите полный путь к исходному CSV-файлу:[/]");
        AnsiConsole.MarkupLine("[grey]Например: C:\\Data\\source.csv[/]");
        AnsiConsole.Write("> ");
        var path = Console.ReadLine()?.Trim().Trim('"');

        if (!string.IsNullOrWhiteSpace(path))
            return path;

        AnsiConsole.MarkupLine("[yellow]Путь не указан. Завершение работы.[/]");
        return null;
    }

    private static void WaitForExit(bool interactiveMode)
    {
        if (!interactiveMode)
            return;

        Console.WriteLine();
        Console.WriteLine("Проверка завершена. Нажмите любую клавишу для выхода...");
        Console.ReadKey(true);
    }

    private static void ShowError(Exception exception)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[red bold]Во время выполнения произошла ошибка:[/]");
        AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
    }
}
