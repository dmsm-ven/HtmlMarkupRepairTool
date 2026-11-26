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
            AnsiConsole.MarkupLine($"[red]Некорректные ID товаров:[/] {Markup.Escape(string.Join(", ", invalidIds))}");
            AnsiConsole.MarkupLine("[grey]Используй положительные числовые ID через запятую.[/]");
            return 1;
        }

        if (requestedIds.Количество == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Список ID пуст.[/]");
            return 1;
        }

        int? languageId = null;
        if (args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]))
        {
            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLanguageId)
                || parsedLanguageId <= 0)
            {
                AnsiConsole.MarkupLine("[red]language_id должен быть положительным целым числом.[/]");
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
            AnsiConsole.MarkupLine("[red]Пути входного CSV и выходного SQL должны отличаться.[/]");
            return 1;
        }

        var delimiter = CsvFileUtility.DetectDelimiter(inputPath);
        var fileSize = new FileInfo(inputPath).Length;

        AnsiConsole.MarkupLine($"[grey]Входной CSV:[/] {Markup.Escape(inputPath)}");
        AnsiConsole.MarkupLine($"[grey]SQL-файл:[/]   {Markup.Escape(outputPath)}");
        AnsiConsole.MarkupLine($"[grey]Товаров:[/]   {requestedIds.Количество:N0}");
        AnsiConsole.MarkupLine($"[grey]Разделитель:[/]  {Markup.Escape(CsvFileUtility.DisplayDelimiter(delimiter))}");

        if (languageId.HasValue)
            AnsiConsole.MarkupLine($"[grey]language_id:[/] {languageId.Value}");
        else
            AnsiConsole.MarkupLine("[yellow]Внимание: без language_id запрос обновит description во всех языковых строках каждого товара.[/]");

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
            AnsiConsole.MarkupLine("[yellow]CSV-файл пуст.[/]");
            return 0;
        }

        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord ?? Array.Empty<string>();
        var productIdIndex = CsvFileUtility.FindHeader(headers, "PRODUCT_ID");
        var descriptionIndex = CsvFileUtility.FindHeader(headers, "Description");

        if (productIdIndex < 0 || descriptionIndex < 0)
        {
            AnsiConsole.MarkupLine("[red]Не найдены обязательные столбцы PRODUCT_ID и Description.[/]");
            AnsiConsole.MarkupLine($"[grey]Обнаруженные заголовки:[/] {Markup.Escape(string.Join(" | ", headers))}");
            return 1;
        }

        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(),
                new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn())
            .Start(context =>
            {
                var task = context.AddTask("[green]Поиск выбранных товаров[/]", maxValue: Math.Max(fileSize, 1));
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
                                    beforeErrors.Количество, afterErrors.Количество));
                            }
                        }
                    }

                    if (processedRows - lastUpdate >= 250)
                    {
                        task.Description =
                            $"[green]Searching products[/] [grey]Строк: {processedСтрок:N0} · Найдено ID: {foundIds.Количество:N0}[/]";
                        lastUpdate = processedRows;
                    }

                    task.Value = Math.Min(inputStream.Position, task.MaxValue);
                }

                task.Value = task.MaxValue;
                task.Description =
                    $"[green]Обработка завершена[/] [grey]Строк: {processedСтрок:N0} · Найдено ID: {foundIds.Количество:N0}[/]";
            });

        // Repeated product IDs are ambiguous without language_id in the export.
        repairedProducts.RemoveAll(product => duplicateIds.Contains(product.ProductId));
        WriteSqlFile(outputPath, repairedProducts, languageId);

        var missingIds = requestedIds.Except(foundIds).OrderBy(id => id).ToList();

        AnsiConsole.WriteLine();
        var summary = new Table().Border(TableBorder.Rounded);
        summary.AddColumn("[grey]Показатель[/]");
        summary.AddColumn(new TableColumn("[grey]Количество[/]").RightAligned());
        summary.AddRow("Строк CSV обработано", processedRows.ToString("N0"));
        summary.AddRow("Requested IDs", requestedIds.Количество.ToString("N0"));
        summary.AddRow("Found IDs", foundIds.Количество.ToString("N0"));
        summary.AddRow("Изменено и включено в SQL", $"[bold green]{repairedProducts.Количество:N0}[/]");
        summary.AddRow("Дублирующихся ID (пропущено)", duplicateIds.Количество > 0
            ? $"[bold yellow]{duplicateIds.Количество:N0}[/]" : "0");
        summary.AddRow("Не найдено ID", missingIds.Количество > 0
            ? $"[bold yellow]{missingIds.Количество:N0}[/]" : "0");
        AnsiConsole.Write(summary);

        if (duplicateIds.Количество > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Пропущены ID с несколькими строками в CSV:[/] {Markup.Escape(string.Join(", ", duplicateIds.OrderBy(id => id)))}");
            AnsiConsole.MarkupLine(
                "[grey]Добавь language_id в экспорт, чтобы различать описания разных языков.[/]");
        }

        if (missingIds.Количество > 0)
            AnsiConsole.MarkupLine($"[yellow]Не найдены ID:[/] {Markup.Escape(string.Join(", ", missingIds))}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]SQL-файл сохранён:[/] {Markup.Escape(outputPath)}");

        if (repairedProducts.Количество == 0)
            AnsiConsole.MarkupLine("[yellow]Изменений не обнаружено. SQL-файл не содержит UPDATE-запросов.[/]");

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

        return invalidIds.Количество == 0;
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
