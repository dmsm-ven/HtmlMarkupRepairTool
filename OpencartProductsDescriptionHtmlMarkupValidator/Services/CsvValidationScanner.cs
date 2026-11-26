using CsvHelper;
using CsvHelper.Configuration;
using HtmlMarkupRepairTool.Infrastructure;
using HtmlMarkupRepairTool.Models;
using Spectre.Console;
using System.Globalization;
using System.Net;
using System.Text;

namespace HtmlMarkupRepairTool.Services;

internal sealed class CsvValidationScanner
{
    private readonly HtmlValidationService _htmlValidator = new();

    public void Scan(string inputPath)
    {
        var delimiter = CsvFileUtility.DetectDelimiter(inputPath);
        var fileSize = new FileInfo(inputPath).Length;
        var outputPath = Path.Combine(
            Path.GetDirectoryName(inputPath)!,
            Path.GetFileNameWithoutExtension(inputPath) + "_errors.csv");

        AnsiConsole.MarkupLine($"[grey]Source CSV:[/] {Markup.Escape(inputPath)}");
        AnsiConsole.MarkupLine($"[grey]Report:[/]      {Markup.Escape(outputPath)}");
        AnsiConsole.MarkupLine($"[grey]Delimiter:[/]   {Markup.Escape(CsvFileUtility.DisplayDelimiter(delimiter))}");
        AnsiConsole.WriteLine();

        var config = CsvFileUtility.CreateConfiguration(delimiter);
        var errorsByProduct = new SortedDictionary<long, HashSet<HtmlValidationError>>();
        var errorCountsByTag = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var allProductIds = new HashSet<long>();
        var processedRows = 0L;
        var rowsWithErrors = 0L;

        using var inputStream = new FileStream(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 64, options: FileOptions.SequentialScan);
        using var inputReader = new StreamReader(
            inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024 * 64, leaveOpen: true);
        using var csvReader = new CsvReader(inputReader, config);

        if (!csvReader.Read())
        {
            AnsiConsole.MarkupLine("[yellow]CSV-файл пуст.[/]");
            return;
        }

        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord ?? Array.Empty<string>();
        var productIdIndex = CsvFileUtility.FindHeader(headers, "PRODUCT_ID");
        var descriptionIndex = CsvFileUtility.FindHeader(headers, "Description");

        if (productIdIndex < 0 || descriptionIndex < 0)
        {
            AnsiConsole.MarkupLine("[red]Не найдены обязательные столбцы PRODUCT_ID и Description.[/]");
            AnsiConsole.MarkupLine($"[grey]Found headers:[/] {Markup.Escape(string.Join(" | ", headers))}");
            return;
        }

        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(),
                new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn())
            .Start(context =>
            {
                var task = context.AddTask("[green]Проверка HTML[/]", maxValue: Math.Max(fileSize, 1));
                var lastUpdate = 0L;

                while (csvReader.Read())
                {
                    processedRows++;
                    var productIdText = csvReader.GetField(productIdIndex)?.Trim() ?? string.Empty;

                    if (!long.TryParse(productIdText, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var productId))
                        continue;

                    allProductIds.Add(productId);
                    var description = csvReader.GetField(descriptionIndex) ?? string.Empty;
                    var errors = _htmlValidator.Check(WebUtility.HtmlDecode(description));

                    if (errors.Count > 0)
                    {
                        rowsWithErrors++;

                        if (!errorsByProduct.TryGetValue(productId, out var productErrors))
                        {
                            productErrors = new HashSet<HtmlValidationError>();
                            errorsByProduct.Add(productId, productErrors);
                        }

                        foreach (var error in errors)
                        {
                            if (!productErrors.Add(error))
                                continue;

                            errorCountsByTag.TryGetValue(error.TagName, out var count);
                            errorCountsByTag[error.TagName] = count + 1;
                        }
                    }

                    if (processedRows - lastUpdate >= 250)
                    {
                        task.Description =
                            $"[green]Проверка HTML[/] [grey]Строк: {processedRows:N0} · Товаров с ошибками: {errorsByProduct.Count:N0}[/]";
                        lastUpdate = processedRows;
                    }

                    task.Value = Math.Min(inputStream.Position, task.MaxValue);
                }

                task.Value = task.MaxValue;
                task.Description =
                    $"[green]Проверка завершена[/] [grey]Строк: {processedRows:N0} · Товаров с ошибками: {errorsByProduct.Count:N0}[/]";
            });

        var selectedTags = SelectTags(errorCountsByTag);
        WriteReport(outputPath, config, errorsByProduct, selectedTags);

        AnsiConsole.WriteLine();
        ShowSummary(processedRows, allProductIds.Count, rowsWithErrors, errorsByProduct.Count);
        ShowProductErrors(errorsByProduct);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Report saved:[/] {Markup.Escape(outputPath)}");
    }

    private static HashSet<string> SelectTags(IReadOnlyDictionary<string, long> errorCountsByTag)
    {
        if (errorCountsByTag.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Ошибок HTML-разметки не обнаружено.[/]");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[grey]Тэг[/]");
        table.AddColumn(new TableColumn("[grey]Количество ошибок[/]").RightAligned());

        foreach (var item in errorCountsByTag.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
        {
            var label = item.Key == "unknown" ? "[yellow]Не определён[/]" : Markup.Escape($"<{item.Key}>");
            table.AddRow(label, item.Value.ToString("N0"));
        }

        AnsiConsole.MarkupLine("[bold]Ошибки по тэгам[/]");
        AnsiConsole.Write(table);

        var tags = errorCountsByTag
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key)
            .ToArray();

        var prompt = new MultiSelectionPrompt<string>()
            .Title("[bold]Выберите тэги, ошибки которых попадут в _errors.csv[/]")
            .NotRequired()
            .PageSize(Math.Min(Math.Max(tags.Length, 5), 20))
            .InstructionsText("[grey](Пробел — отметить/снять, Enter — продолжить)[/]")
            .AddChoices(tags)
            .UseConverter(tag => tag == "unknown" ? "Не определённый тэг" : $"<{tag}>");

        foreach (var tag in tags)
            prompt.Select(tag);

        return AnsiConsole.Prompt(prompt).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteReport(
        string outputPath,
        CsvConfiguration config,
        IReadOnlyDictionary<long, HashSet<HtmlValidationError>> errorsByProduct,
        HashSet<string> selectedTags)
    {
        using var outputWriter = new StreamWriter(
            outputPath, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csvWriter = new CsvWriter(outputWriter, config);

        csvWriter.WriteField("PRODUCT_ID");
        csvWriter.WriteField("ERROR_COUNT");
        csvWriter.WriteField("ERRORS");
        csvWriter.NextRecord();

        foreach (var product in errorsByProduct)
        {
            var errors = product.Value
                .Where(error => selectedTags.Contains(error.TagName))
                .OrderBy(error => error.TagName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(error => error.Message, StringComparer.Ordinal)
                .ToArray();

            if (errors.Length == 0)
                continue;

            csvWriter.WriteField(product.Key);
            csvWriter.WriteField(errors.Length);
            csvWriter.WriteField(string.Join("; ", errors.Select(error => error.Message)));
            csvWriter.NextRecord();
        }
    }

    private static void ShowProductErrors(
        IReadOnlyDictionary<long, HashSet<HtmlValidationError>> errorsByProduct)
    {
        if (errorsByProduct.Count == 0)
            return;

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[grey]PRODUCT_ID[/]");
        table.AddColumn(new TableColumn("[grey]Ошибок[/]").RightAligned());
        table.AddColumn("[grey]Описание ошибок[/]");

        foreach (var product in errorsByProduct)
        {
            var errors = product.Value.OrderBy(error => error.Message, StringComparer.Ordinal).ToArray();
            table.AddRow(
                product.Key.ToString(CultureInfo.InvariantCulture),
                errors.Length.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(string.Join("; ", errors.Select(error => error.Message))));
        }

        AnsiConsole.Write(table);
    }

    private static void ShowSummary(
        long processedRows, int uniqueProducts, long rowsWithErrors, int productsWithErrors)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[grey]Показатель[/]");
        table.AddColumn(new TableColumn("[grey]Количество[/]").RightAligned());
        table.AddRow("Строк CSV обработано", processedRows.ToString("N0"));
        table.AddRow("Уникальных товаров", uniqueProducts.ToString("N0"));
        table.AddRow("Строк с ошибками", rowsWithErrors.ToString("N0"));
        table.AddRow("Товаров с ошибками", $"[bold red]{productsWithErrors:N0}[/]");
        table.AddRow("Товаров без ошибок", (uniqueProducts - productsWithErrors).ToString("N0"));
        AnsiConsole.Write(table);
    }
}
