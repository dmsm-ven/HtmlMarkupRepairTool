using CsvHelper;
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
            AnsiConsole.MarkupLine("[yellow]CSV file is empty.[/]");
            return;
        }

        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord ?? Array.Empty<string>();
        var productIdIndex = CsvFileUtility.FindHeader(headers, "PRODUCT_ID");
        var descriptionIndex = CsvFileUtility.FindHeader(headers, "Description");

        if (productIdIndex < 0 || descriptionIndex < 0)
        {
            AnsiConsole.MarkupLine("[red]Required columns PRODUCT_ID and Description were not found.[/]");
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
                var task = context.AddTask("[green]Validating HTML[/]", maxValue: Math.Max(fileSize, 1));
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
                            $"[green]HTML validation[/] [grey]Rows: {processedRows:N0} · Products with errors: {errorsByProduct.Count:N0}[/]";
                        lastUpdate = processedRows;
                    }

                    task.Value = Math.Min(inputStream.Position, task.MaxValue);
                }

                task.Value = task.MaxValue;
                task.Description =
                    $"[green]Validation complete[/] [grey]Rows: {processedRows:N0} · Products with errors: {errorsByProduct.Count:N0}[/]";
            });

        var selectedTags = SelectTags(errorCountsByTag);
        WriteReport(outputPath, config, errorsByProduct, selectedTags);

        AnsiConsole.WriteLine();
        ShowSummary(processedRows, allProductIds.Count, rowsWithErrors, errorsByProduct.Count);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Report saved:[/] {Markup.Escape(outputPath)}");
    }

    private static HashSet<string> SelectTags(IReadOnlyDictionary<string, long> errorCountsByTag)
    {
        if (errorCountsByTag.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No HTML markup errors were found.[/]");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[grey]Tag[/]");
        table.AddColumn(new TableColumn("[grey]Error count[/]").RightAligned());

        foreach (var item in errorCountsByTag.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
        {
            var label = item.Key == "unknown" ? "[yellow]Unknown[/]" : Markup.Escape($"<{item.Key}>");
            table.AddRow(label, item.Value.ToString("N0"));
        }

        AnsiConsole.MarkupLine("[bold]Errors by tag[/]");
        AnsiConsole.Write(table);

        var tags = errorCountsByTag
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key)
            .ToArray();

        var prompt = new MultiSelectionPrompt<string>()
            .Title("[bold]Select tags to include in _errors.csv[/]")
            .NotRequired()
            .PageSize(Math.Min(Math.Max(tags.Length, 5), 20))
            .InstructionsText("[grey](Space — toggle, Enter — continue)[/]")
            .AddChoices(tags)
            .UseConverter(tag => tag == "unknown" ? "Unknown tag" : $"<{tag}>");

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

    private static void ShowSummary(
        long processedRows, int uniqueProducts, long rowsWithErrors, int productsWithErrors)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[grey]Metric[/]");
        table.AddColumn(new TableColumn("[grey]Count[/]").RightAligned());
        table.AddRow("CSV rows processed", processedRows.ToString("N0"));
        table.AddRow("Unique products", uniqueProducts.ToString("N0"));
        table.AddRow("Rows with errors", rowsWithErrors.ToString("N0"));
        table.AddRow("Products with errors", $"[bold red]{productsWithErrors:N0}[/]");
        table.AddRow("Products without errors", (uniqueProducts - productsWithErrors).ToString("N0"));
        AnsiConsole.Write(table);
    }
}
