namespace HtmlMarkupRepairTool.Models;

public sealed record HtmlValidationError(string ErrorCode, string TagName, string Message);
