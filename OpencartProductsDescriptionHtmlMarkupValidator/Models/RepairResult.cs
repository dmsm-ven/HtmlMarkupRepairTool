namespace HtmlMarkupRepairTool.Models;

public sealed record RepairResult(
    long ProductId,
    string OriginalDescription,
    string RepairedDescription,
    int ErrorsBefore,
    int ErrorsAfter);
