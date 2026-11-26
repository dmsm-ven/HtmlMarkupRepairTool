using CsvHelper.Configuration;
using System.Globalization;
using System.Text;

namespace HtmlMarkupRepairTool.Infrastructure;

internal static class CsvFileUtility
{
    public static CsvConfiguration CreateConfiguration(string delimiter) =>
        new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Delimiter = delimiter,
            DetectDelimiter = false,
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.None,
            BufferSize = 1024 * 64
        };

    public static string DetectDelimiter(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var firstLine = reader.ReadLine() ?? string.Empty;
        var candidates = new[] { ",", ";", "\t" };

        return candidates
            .Select(candidate => new
            {
                Delimiter = candidate,
                Count = CountOutsideQuotes(firstLine, candidate[0])
            })
            .OrderByDescending(item => item.Count)
            .First()
            .Delimiter;
    }

    public static int FindHeader(string[] headers, string expected)
    {
        for (var index = 0; index < headers.Length; index++)
        {
            if (string.Equals(headers[index]?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    public static string DisplayDelimiter(string delimiter) => delimiter == "\t" ? "TAB" : delimiter;

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var insideQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                if (insideQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                insideQuotes = !insideQuotes;
            }
            else if (!insideQuotes && line[index] == delimiter)
            {
                count++;
            }
        }

        return count;
    }
}
