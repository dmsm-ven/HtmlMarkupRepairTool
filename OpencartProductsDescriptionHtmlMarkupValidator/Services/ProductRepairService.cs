using CsvHelper;
using HtmlMarkupRepairTool.Infrastructure;
using HtmlMarkupRepairTool.Models;
using Spectre.Console;
using System.Globalization;
using System.Net;
using System.Text;

namespace HtmlMarkupRepairTool.Services;

internal sealed class ProductRepairService
{
    private readonly HtmlValidationService _htmlValidator = new();

    public int Run(string inputPath, string[] args)
    {
        if (!TryParseProductIds(args[1], out var requestedIds, out var invalidIds))
        {
            AnsiConsole.MarkupLine($"[red]Invalid product IDs:[/] {Markup.Escape(string.Join(", ", invalidIds))}");
            AnsiConsole.MarkupLine("[grey]Use positive numeric IDs separated by commas.[/]");
            return 1;
        }

        if (requestedIds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]The product ID list is empty.[/]");
            return 1;
        }

        int? languageId = null;
        if (args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]))
        {
            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLanguageId)
                || parsedLanguageId <= 0)
            {
                AnsiConsole.MarkupLine("[red]language_id must be a positive integer.[/]");
                return 1;
            }

            languageId = parsedLanguageId;
        }

        var outputPath = args.Length >= 4 && !string.IsNullOrWhiteSpace(args[3])
            ? Path.GetFullPath(args[3])
            : Path.Combine(Path.GetDirectoryName(inputPath)!,
                Path.GetFileNameWithoutExtension(inputPath) + "_repairs.sql");

        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]Input CSV and output SQL paths must be different.[/]");
            return 1;
        }

        var delimiter = CsvFileUtility.DetectDelimiter(inputPath);
        var fileSize = new FileInfo(inputPath).Length;

        AnsiConsole.MarkupLine($"[grey]Input CSV:[/] {Markup.Escape(inputPath)}");
        AnsiConsole.MarkupLine($"[grey]SQL file:[/]   {Markup.Escape(outputPath)}");
        AnsiConsole.MarkupLine($"[grey]Products:[/]   {requestedIds.Count:N0}");
        AnsiConsole.MarkupLine($"[grey]Delimiter:[/]  {Markup.Escape(CsvFileUtility.DisplayDelimiter(delimiter))}");

        if (languageId.HasValue)
            AnsiConsole.MarkupLine($"[grey]language_id:[/] {languageId.Value}");
        else
            AnsiConsole.MarkupLine("[yellow]Warning: without language_id, UPDATE affects all language rows for each product.[/]");

        AnsiConsole.WriteLine();

        var config = CsvFileUtility.CreateConfiguration(delimiter);
        var repairedProducts = new List<RepairResult>();
        var foundIds = new HashSet<long>();
        var duplicateIds = new HashSet<long>();
        var processedRows = 0L;

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
            return 0;
        }

        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord ?? Array.Empty<string>();
        var productIdIndex = CsvFileUtility.FindHeader(headers, "PRODUCT_ID");
        var descriptionIndex = CsvFileUtility.FindHeader(headers, "Description");

        if (productIdIndex < 0 || descriptionIndex < 0)
        {
            AnsiConsole.MarkupLine("[red]Required columns PRODUCT_ID and Description were not found.[/]");
            AnsiConsole.MarkupLine($"[grey]Found headers:[/] {Markup.Escape(string.Join(" | ", headers))}");
            return 1;
        }

        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(),
                new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn())
            .Start(context =>
            {
                var task = context.AddTask("[green]Searching selected products[/]", maxValue: Math.Max(fileSize, 1));
                var lastUpdate = 0L;

                while (csvReader.Read())
                {
                    processedRows++;
                    var productIdText = csvReader.GetField(productIdIndex)?.Trim() ?? string.Empty;

                    if (long.TryParse(productIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var productId)
                        && requestedIds.Contains(productId))
                    {
                        if (!foundIds.Add(productId))
                        {
                            duplicateIds.Add(productId);
                        }
                        else
                        {
                            var sourceDescription = csvReader.GetField(descriptionIndex) ?? string.Empty;
                            var decodedDescription = WebUtility.HtmlDecode(sourceDescription);
                            var beforeErrors = _htmlValidator.Check(decodedDescription);
                            var repairedDescription = _htmlValidator.Repair(decodedDescription);
                            var afterErrors = _htmlValidator.Check(repairedDescription);
                            var encodedRepairedDescription = WebUtility.HtmlEncode(repairedDescription);

                            if (!string.Equals(sourceDescription, encodedRepairedDescription, StringComparison.Ordinal))
                            {
                                repairedProducts.Add(new RepairResult(
                                    productId, sourceDescription, encodedRepairedDescription,
                                    beforeErrors.Count, afterErrors.Count));
                            }
                        }
                    }

                    if (processedRows - lastUpdate >= 250)
                    {
                        task.Description =
                            $"[green]Searching products[/] [grey]Rows: {processedRows:N0} · IDs found: {foundIds.Count:N0}[/]";
                        lastUpdate = processedRows;
                    }

                    task.Value = Math.Min(inputStream.Position, task.MaxValue);
                }

                task.Value = task.MaxValue;
                task.Description =
                    $"[green]Search complete[/] [grey]Rows: {processedRows:N0} · IDs found: {foundIds.Count:N0}[/]";
            });

        // Repeated product IDs are ambiguous without language_id in the export.
        repairedProducts.RemoveAll(product => duplicateIds.Contains(product.ProductId));
        WriteSqlFile(outputPath, repairedProducts, languageId);

        var missingIds = requestedIds.Except(foundIds).OrderBy(id => id).ToList();

        AnsiConsole.WriteLine();
        var summary = new Table().Border(TableBorder.Rounded);
        summary.AddColumn("[grey]Metric[/]");
        summary.AddColumn(new TableColumn("[grey]Count[/]").RightAligned());
        summary.AddRow("CSV rows processed", processedRows.ToString("N0"));
        summary.AddRow("Requested IDs", requestedIds.Count.ToString("N0"));
        summary.AddRow("Found IDs", foundIds.Count.ToString("N0"));
        summary.AddRow("Modified and included in SQL", $"[bold green]{repairedProducts.Count:N0}[/]");
        summary.AddRow("Duplicate IDs (skipped)", duplicateIds.Count > 0
            ? $"[bold yellow]{duplicateIds.Count:N0}[/]" : "0");
        summary.AddRow("Missing IDs", missingIds.Count > 0
            ? $"[bold yellow]{missingIds.Count:N0}[/]" : "0");
        AnsiConsole.Write(summary);

        if (duplicateIds.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Skipped IDs with multiple CSV rows:[/] {Markup.Escape(string.Join(", ", duplicateIds.OrderBy(id => id)))}");
            AnsiConsole.MarkupLine(
                "[grey]Include language_id in the export to distinguish descriptions for different languages.[/]");
        }

        if (missingIds.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]IDs not found:[/] {Markup.Escape(string.Join(", ", missingIds))}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]SQL file saved:[/] {Markup.Escape(outputPath)}");

        if (repairedProducts.Count == 0)
            AnsiConsole.MarkupLine("[yellow]No changes detected. The SQL file contains no UPDATE statements.[/]");

        return 0;
    }

    private static bool TryParseProductIds(
        string input, out HashSet<long> ids, out List<string> invalidIds)
    {
        ids = new HashSet<long>();
        invalidIds = new List<string>();

        foreach (var token in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                ids.Add(id);
            else
                invalidIds.Add(token);
        }

        return invalidIds.Count == 0;
    }

    private static void WriteSqlFile(
        string outputPath, IReadOnlyCollection<RepairResult> products, int? languageId)
    {
        using var writer = new StreamWriter(
            outputPath, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        writer.WriteLine("-- Generated by HtmlDescriptionChecker.");
        writer.WriteLine("-- Review this file before executing it against the production database.");
        writer.WriteLine("-- Description values are encoded as UTF-8 hex to avoid SQL quote/backslash escaping issues.");

        if (languageId.HasValue)
            writer.WriteLine($"-- Updates only language_id = {languageId.Value}.");
        else
            writer.WriteLine("-- WARNING: No language_id was supplied; each UPDATE affects ALL language rows for that product_id.");

        writer.WriteLine();
        writer.WriteLine("START TRANSACTION;");
        writer.WriteLine();

        foreach (var product in products.OrderBy(product => product.ProductId))
        {
            var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(product.RepairedDescription));
            writer.WriteLine($"-- product_id={product.ProductId}; HTML parse errors before={product.ErrorsBefore}; after={product.ErrorsAfter}");
            writer.WriteLine("UPDATE `oc_product_description`");
            writer.WriteLine($"SET `description` = CONVERT(0x{hex} USING utf8mb4)");
            writer.WriteLine($"WHERE `product_id` = {product.ProductId}{(languageId.HasValue ? $" AND `language_id` = {languageId.Value}" : string.Empty)};");
            writer.WriteLine();
        }

        writer.WriteLine("COMMIT;");
    }
}
