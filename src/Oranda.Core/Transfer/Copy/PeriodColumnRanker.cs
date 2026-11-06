using Oranda.Core.Metadata;

namespace Oranda.Core.Transfer.Copy;

public sealed record PeriodColumnCandidate(ColumnInfo Column, bool IsCreatedAt, string? DefaultFormat);

/// <summary>기간 열 후보를 고른다. 업무 날짜 열이 먼저, 생성 시각 열이 뒤에 온다.</summary>
public static class PeriodColumnRanker
{
    private static readonly HashSet<string> CreatedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATED_AT", "CREATE_DT", "CREATE_DTM", "CREATED_DATE", "CRT_DT", "CRT_DTM",
        "REG_DT", "REG_DTM", "INS_DT", "INS_DTM", "FRST_REG_DTM", "FIRST_REG_DTM",
    };

    public static IReadOnlyList<PeriodColumnCandidate> Rank(IReadOnlyList<ColumnInfo> columns)
    {
        var business = new List<PeriodColumnCandidate>();
        var created = new List<PeriodColumnCandidate>();
        foreach (var c in columns)
        {
            var type = c.DataType.Trim().ToUpperInvariant();
            string? format = null;
            if (type == "DATE" || type.StartsWith("TIMESTAMP", StringComparison.Ordinal)) { }
            else if (type is "VARCHAR2" or "CHAR" || type.StartsWith("VARCHAR2(", StringComparison.Ordinal) || type.StartsWith("CHAR(", StringComparison.Ordinal))
            {
                var length = c.Length ?? ParseLength(type);
                format = length is { } n ? DateStringFormat.GuessFromLength(n) : null;
                if (format is null) continue;
            }
            else continue;

            var isCreated = CreatedNames.Contains(c.Name);
            (isCreated ? created : business).Add(new PeriodColumnCandidate(c, isCreated, format));
        }
        return [.. business, .. created];
    }

    private static int? ParseLength(string type)
    {
        var open = type.IndexOf('(');
        var close = type.IndexOf(')');
        if (open < 0 || close < open) return null;
        var digits = new string(type[(open + 1)..close].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }
}
