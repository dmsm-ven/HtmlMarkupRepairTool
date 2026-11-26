using AngleSharp.Html.Parser;
using HtmlAgilityPack;
using HtmlMarkupRepairTool.Models;
using System.Text.RegularExpressions;

namespace HtmlMarkupRepairTool.Services;

internal sealed class HtmlValidationService
{
    public List<HtmlValidationError> Check(string html)
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
            .Select(error =>
            {
                var tagName = ExtractErrorTagName(error.Reason) ?? "unknown";
                var tagLabel = tagName == "unknown" ? tagName : $"<{tagName}>";
                var message = $"{error.Code} {tagLabel} (line {error.Line}, position {error.LinePosition})";
                return new HtmlValidationError(error.Code.ToString(), tagName, message);
            })
            .Distinct()
            .ToList();
    }

    public string Repair(string html)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument("<!doctype html><html><body></body></html>");
        var contextElement = document.CreateElement("div");

        foreach (var node in parser.ParseFragment(html, contextElement).ToArray())
            contextElement.AppendChild(node);

        return contextElement.InnerHtml;
    }

    private static string? ExtractErrorTagName(string? reason)
    {
        var match = Regex.Match(reason ?? string.Empty, @"</?\s*([a-zA-Z][a-zA-Z0-9:_-]*)\b");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }
}
