using AngleSharp.Html.Parser;
using CsvHelper;
using CsvHelper.Configuration;
using HtmlAgilityPack;
using Spectre.Console;
using System.Globalization;
using System.Net;
using System.Text;

// Usage:
//   HtmlDescriptionChecker.exe "products.csv" "12540,18732,25119"
//   HtmlDescriptionChecker.exe "products.csv" "12540,18732" 1
//   HtmlDescriptionChecker.exe "products.csv" "12540,18732" 1 "repairs.sql"
//
// Arguments:
//   1. CSV exported from DBeaver; columns PRODUCT_ID and Description.
//   2. Comma-separated product IDs to repair.
//   3. Optional OpenCart language_id. If omitted, UPDATE affects all language rows
//      for each product_id.
//   4. Optional output SQL file path.
//
// The program never connects to or modifies the database.

Console.OutputEncoding = Encoding.UTF8;

AnsiConsole.MarkupLine("[bold dodgerblue1]OpenCart Description Repair[/]");
AnsiConsole.MarkupLine("[grey]Нормализация HTML через AngleSharp (HTML5 parser)[/]");
AnsiConsole.WriteLine();

if (args.Length < 2)
{
    AnsiConsole.MarkupLine("[grey]Использование:[/]");
    AnsiConsole.WriteLine("  HtmlDescriptionChecker.exe \"products.csv\" \"12540,18732,25119\"");
    AnsiConsole.WriteLine("  HtmlDescriptionChecker.exe \"products.csv\" \"12540,18732\" 1");
    AnsiConsole.WriteLine("  HtmlDescriptionChecker.exe \"products.csv\" \"12540,18732\" 1 \"repairs.sql\"");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[grey]Аргумент 3 (необязательно): language_id OpenCart.[/]");
    AnsiConsole.MarkupLine("[grey]Если он не указан, UPDATE затронет все языковые строки товара.[/]");
    return;
}

var inputPath = Path.GetFullPath(args[0]);

if (!File.Exists(inputPath))
{
    AnsiConsole.MarkupLine($"[red]Файл не найден:[/] {Markup.Escape(inputPath)}");
    return;
}

if (!TryParseProductIds(args[1], out var requestedIds, out var invalidIds))
{
    AnsiConsole.MarkupLine($"[red]Некорректные ID товаров:[/] {Markup.Escape(string.Join(", ", invalidIds))}");
    AnsiConsole.MarkupLine("[grey]Используй положительные числовые ID через запятую.[/]");
    return;
}

if (requestedIds.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]Список ID пуст.[/]");
    return;
}

int? languageId = null;

if (args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]))
{
    if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLanguageId)
        || parsedLanguageId <= 0)
    {
        AnsiConsole.MarkupLine("[red]language_id должен быть положительным целым числом.[/]");
        return;
    }

    languageId = parsedLanguageId;
}

var outputPath = args.Length >= 4 && !string.IsNullOrWhiteSpace(args[3])
    ? Path.GetFullPath(args[3])
    : Path.Combine(
        Path.GetDirectoryName(inputPath)!,
        Path.GetFileNameWithoutExtension(inputPath) + "_repairs.sql");

if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
{
    AnsiConsole.MarkupLine("[red]Пути входного CSV и выходного SQL должны отличаться.[/]");
    return;
}

try
{
    var delimiter = DetectDelimiter(inputPath);
    var fileSize = new FileInfo(inputPath).Length;

    AnsiConsole.MarkupLine($"[grey]Входной CSV:[/] {Markup.Escape(inputPath)}");
    AnsiConsole.MarkupLine($"[grey]SQL-файл:[/]    {Markup.Escape(outputPath)}");
    AnsiConsole.MarkupLine($"[grey]Товаров:[/]     {requestedIds.Count:N0}");
    AnsiConsole.MarkupLine($"[grey]Разделитель:[/] {Markup.Escape(DisplayDelimiter(delimiter))}");

    if (languageId.HasValue)
    {
        AnsiConsole.MarkupLine($"[grey]language_id:[/]  {languageId.Value}");
    }
    else
    {
        AnsiConsole.MarkupLine(
            "[yellow]Внимание: без language_id запрос обновит description во всех языковых строках каждого товара.[/]");
    }

    AnsiConsole.WriteLine();

    var config = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        Delimiter = delimiter,
        DetectDelimiter = false,
        IgnoreBlankLines = true,
        TrimOptions = TrimOptions.None,
        BufferSize = 1024 * 64
    };

    var repairedProducts = new List<RepairResult>();
    var foundIds = new HashSet<long>();
    var duplicateIds = new HashSet<long>();
    var processedRows = 0L;
    var selectedRows = 0L;

    using var inputStream = new FileStream(
        inputPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1024 * 64,
        options: FileOptions.SequentialScan);

    using var inputReader = new StreamReader(
        inputStream,
        Encoding.UTF8,
        detectEncodingFromByteOrderMarks: true,
        bufferSize: 1024 * 64,
        leaveOpen: true);

    using var csvReader = new CsvReader(inputReader, config);

    if (!csvReader.Read())
    {
        AnsiConsole.MarkupLine("[yellow]CSV-файл пуст.[/]");
        return;
    }

    csvReader.ReadHeader();

    var headers = csvReader.HeaderRecord ?? Array.Empty<string>();
    var productIdIndex = FindHeader(headers, "PRODUCT_ID");
    var descriptionIndex = FindHeader(headers, "Description");

    if (productIdIndex < 0 || descriptionIndex < 0)
    {
        AnsiConsole.MarkupLine("[red]Не найдены обязательные столбцы PRODUCT_ID и Description.[/]");
        AnsiConsole.MarkupLine($"[grey]Обнаруженные заголовки:[/] {Markup.Escape(string.Join(" | ", headers))}");
        return;
    }

    AnsiConsole.Progress()
        .AutoClear(false)
        .HideCompleted(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new RemainingTimeColumn(),
            new SpinnerColumn())
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
                        // The CSV contains multiple rows for this product_id.
                        // Without language_id in the CSV we cannot safely determine
                        // which description belongs to which language.
                        duplicateIds.Add(productId);
                    }
                    else
                    {
                        selectedRows++;

                        var sourceDescription = csvReader.GetField(descriptionIndex) ?? string.Empty;
                        var decodedDescription = WebUtility.HtmlDecode(sourceDescription);

                        var beforeErrors = CheckHtml(decodedDescription);
                        var repairedDescription = RepairHtmlFragment(decodedDescription);
                        var afterErrors = CheckHtml(repairedDescription);

                        // The database stores this field HTML-entity-encoded.
                        // Encode the normalized markup back to the same storage format.
                        var encodedRepairedDescription = WebUtility.HtmlEncode(repairedDescription);

                        if (!string.Equals(sourceDescription, encodedRepairedDescription, StringComparison.Ordinal))
                        {
                            repairedProducts.Add(new RepairResult(
                                productId,
                                sourceDescription,
                                encodedRepairedDescription,
                                beforeErrors.Count,
                                afterErrors.Count));
                        }
                    }
                }

                if (processedRows - lastUpdate >= 250)
                {
                    task.Description =
                        $"[green]Поиск товаров[/] [grey]Строк: {processedRows:N0} · Найдено ID: {foundIds.Count:N0}[/]";
                    lastUpdate = processedRows;
                }

                task.Value = Math.Min(inputStream.Position, task.MaxValue);
            }

            task.Value = task.MaxValue;
            task.Description =
                $"[green]Обработка завершена[/] [grey]Строк: {processedRows:N0} · Найдено ID: {foundIds.Count:N0}[/]";
        });

    // Products repeated in the CSV are ambiguous when language_id is not part
    // of the export. Do not generate an UPDATE for them.
    repairedProducts.RemoveAll(product => duplicateIds.Contains(product.ProductId));

    WriteSqlFile(outputPath, repairedProducts, languageId);

    var missingIds = requestedIds.Except(foundIds).OrderBy(id => id).ToList();

    AnsiConsole.WriteLine();

    var summary = new Table().Border(TableBorder.Rounded);
    summary.AddColumn("[grey]Показатель[/]");
    summary.AddColumn(new TableColumn("[grey]Количество[/]").RightAligned());
    summary.AddRow("Строк CSV обработано", processedRows.ToString("N0"));
    summary.AddRow("Запрошено ID", requestedIds.Count.ToString("N0"));
    summary.AddRow("Найдено ID", foundIds.Count.ToString("N0"));
    summary.AddRow("Изменено и включено в SQL", $"[bold green]{repairedProducts.Count:N0}[/]");
    summary.AddRow("Дублирующихся ID (пропущено)", duplicateIds.Count > 0
        ? $"[bold yellow]{duplicateIds.Count:N0}[/]"
        : "0");
    summary.AddRow("Не найдено ID", missingIds.Count > 0
        ? $"[bold yellow]{missingIds.Count:N0}[/]"
        : "0");

    AnsiConsole.Write(summary);

    if (duplicateIds.Count > 0)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Пропущены ID с несколькими строками в CSV:[/] {Markup.Escape(string.Join(", ", duplicateIds.OrderBy(x => x)))}");
        AnsiConsole.MarkupLine(
            "[grey]Добавь language_id в экспорт, чтобы различать описания разных языков.[/]");
    }

    if (missingIds.Count > 0)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Не найдены ID:[/] {Markup.Escape(string.Join(", ", missingIds))}");
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[green]SQL-файл сохранён:[/] {Markup.Escape(outputPath)}");

    if (repairedProducts.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]Изменений не обнаружено. SQL-файл не содержит UPDATE-запросов.[/]");
    }
}
catch (Exception ex)
{
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[red bold]Во время выполнения произошла ошибка:[/]");
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
}

static bool TryParseProductIds(
    string input,
    out HashSet<long> ids,
    out List<string> invalidIds)
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

static string RepairHtmlFragment(string decodedHtml)
{
    var parser = new HtmlParser();

    // Parse as an HTML fragment in a DIV context, similar to an editor body.
    var document = parser.ParseDocument("<!doctype html><html><body></body></html>");
    var contextElement = document.CreateElement("div");

    var nodes = parser.ParseFragment(decodedHtml, contextElement).ToArray();

    foreach (var node in nodes)
    {
        contextElement.AppendChild(node);
    }

    return contextElement.InnerHtml;
}

static List<string> CheckHtml(string html)
{
    var document = new HtmlDocument
    {
        OptionCheckSyntax = true,
        OptionAutoCloseOnEnd = false,
        OptionExtractErrorSourceText = false
    };

    document.LoadHtml(html);

    return document.ParseErrors
        .Where(error => error.Code is
            HtmlParseErrorCode.TagNotClosed or
            HtmlParseErrorCode.TagNotOpened or
            HtmlParseErrorCode.EndTagInvalidHere)
        .Select(error => $"{error.Code} (line {error.Line}, position {error.LinePosition})")
        .Distinct()
        .ToList();
}

static void WriteSqlFile(
    string outputPath,
    IReadOnlyCollection<RepairResult> products,
    int? languageId)
{
    using var writer = new StreamWriter(
        outputPath,
        append: false,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    writer.WriteLine("-- Generated by HtmlDescriptionChecker.");
    writer.WriteLine("-- Review this file before executing it against the production database.");
    writer.WriteLine("-- Description values are encoded as UTF-8 hex to avoid SQL quote/backslash escaping issues.");

    if (languageId.HasValue)
    {
        writer.WriteLine($"-- Updates only language_id = {languageId.Value}.");
    }
    else
    {
        writer.WriteLine("-- WARNING: No language_id was supplied; each UPDATE affects ALL language rows for that product_id.");
    }

    writer.WriteLine();
    writer.WriteLine("START TRANSACTION;");
    writer.WriteLine();

    foreach (var product in products.OrderBy(product => product.ProductId))
    {
        var utf8Bytes = Encoding.UTF8.GetBytes(product.RepairedDescription);
        var hex = Convert.ToHexString(utf8Bytes);

        writer.WriteLine($"-- product_id={product.ProductId}; HTML parse errors before={product.ErrorsBefore}; after={product.ErrorsAfter}");
        writer.WriteLine("UPDATE `oc_product_description`");
        writer.WriteLine($"SET `description` = CONVERT(0x{hex} USING utf8mb4)");
        writer.WriteLine($"WHERE `product_id` = {product.ProductId}{(languageId.HasValue ? $" AND `language_id` = {languageId.Value}" : string.Empty)};");
        writer.WriteLine();
    }

    writer.WriteLine("COMMIT;");
}

static int FindHeader(string[] headers, string expected)
{
    for (var i = 0; i < headers.Length; i++)
    {
        if (string.Equals(headers[i]?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            return i;
    }

    return -1;
}

static string DetectDelimiter(string path)
{
    using var reader = new StreamReader(
        path,
        Encoding.UTF8,
        detectEncodingFromByteOrderMarks: true);

    var firstLine = reader.ReadLine() ?? string.Empty;
    var candidates = new[] { ",", ";", "\t" };

    return candidates
        .Select(candidate => new
        {
            Delimiter = candidate,
            Count = CountOutsideQuotes(firstLine, candidate[0])
        })
        .OrderByDescending(x => x.Count)
        .First().Delimiter;
}

static int CountOutsideQuotes(string line, char delimiter)
{
    var count = 0;
    var insideQuotes = false;

    for (var i = 0; i < line.Length; i++)
    {
        if (line[i] == '"')
        {
            if (insideQuotes && i + 1 < line.Length && line[i + 1] == '"')
            {
                i++;
                continue;
            }

            insideQuotes = !insideQuotes;
        }
        else if (!insideQuotes && line[i] == delimiter)
        {
            count++;
        }
    }

    return count;
}

static string DisplayDelimiter(string delimiter) =>
    delimiter == "\t" ? "TAB" : delimiter;

sealed record RepairResult(
    long ProductId,
    string OriginalDescription,
    string RepairedDescription,
    int ErrorsBefore,
    int ErrorsAfter);
