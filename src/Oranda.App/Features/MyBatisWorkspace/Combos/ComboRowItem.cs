using System.Text;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>조합 표의 조건 칸 하나(켬·끔).</summary>
public sealed record ComboStateCell(bool IsOn)
{
    public string Text => IsOn ? ComboText.On : ComboText.Off;
}

/// <summary>
/// 「조합 검사」 표의 줄 하나(<see cref="ComboCheckResult"/> 를 화면 글로 바꾼 것). 오른쪽 칸은 고른 줄의 펼친 SQL 을
/// 오류 앞·오류·오류 뒤 세 조각으로 나눠 오류 자리를 강조한다(빈 줄은 뺀다).
/// </summary>
public sealed class ComboRowItem
{
    private const char Open = '\u0001';
    private const char Close = '\u0002';

    public ComboRowItem(ComboCheckResult result, IReadOnlyList<ComboCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(conditions);
        Result = result;
        States = [.. result.States.Select(s => new ComboStateCell(s))];
        SqlLine = string.Join(' ', result.Sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var parts = conditions.Select((c, i) => $"{Short(c.Label)} {(i < result.States.Count && result.States[i] ? ComboText.On : ComboText.Off)}");
        Heading = conditions.Count == 0 ? result.Title : $"{result.Title} · {string.Join(", ", parts)}";
        (SqlBefore, SqlError, SqlAfter) = Split(result);
    }

    public ComboCheckResult Result { get; }

    public int Number => Result.Number;

    public IReadOnlyList<ComboStateCell> States { get; }

    public bool IsError => Result.Outcome == ComboOutcome.Error;

    public bool IsWarning => Result.Outcome == ComboOutcome.Warning;

    public bool IsPass => Result.Outcome == ComboOutcome.Pass;

    /// <summary>결과 칸(✓ 통과, ✕ 이유, ! 주의).</summary>
    public string ResultText => Result.Outcome switch
    {
        ComboOutcome.Pass => "✓ " + Result.Reason,
        ComboOutcome.Warning => "! " + Result.Reason,
        _ => "✕ " + Result.Reason,
    };

    /// <summary>표의 펼친 SQL 칸(공백을 한 칸으로 줄인 한 줄).</summary>
    public string SqlLine { get; }

    /// <summary>오른쪽 칸 제목(「조합 2 · sal 켬, comm 끔」).</summary>
    public string Heading { get; }

    public string SqlBefore { get; }

    public string SqlError { get; }

    public string SqlAfter { get; }

    public string Detail => Result.Detail;

    public bool HasDetail => Result.Detail.Length > 0;

    public bool CanShowInEditor => Result.SourceLine is not null;

    // 열 머리와 같은 모양에서 「sal != null」 → 「sal」(점 경로·글자만인 첫 낱말). 못 줄이면 그대로.
    private static string Short(string label)
    {
        var first = label.Split(' ', 2)[0];
        return first.Length > 0 && first.All(c => char.IsLetterOrDigit(c) || c is '_' or '.')
            ? first
            : label;
    }

    private static (string Before, string Error, string After) Split(ComboCheckResult result)
    {
        var sql = result.Sql;
        if (result.ErrorRange is { } r && r.Start >= 0 && r.End <= sql.Length)
        {
            sql = sql[..r.Start] + Open + sql.Substring(r.Start, r.Length) + Close + sql[r.End..];
        }

        var text = WithoutBlankLines(sql);
        int open = text.IndexOf(Open, StringComparison.Ordinal);
        int close = text.IndexOf(Close, StringComparison.Ordinal);
        return open < 0 || close < open
            ? (text, string.Empty, string.Empty)
            : (text[..open], text[(open + 1)..close], text[(close + 1)..]);
    }

    // 끈 블록이 남긴 빈 줄을 빼고 공통 들여쓰기를 줄인다.
    private static string WithoutBlankLines(string sql)
    {
        var lines = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => l.Any(c => !char.IsWhiteSpace(c) && c != Open && c != Close)).ToList();
        int indent = lines.Count == 0 ? 0 : lines.Min(l => l.TakeWhile(c => c == ' ').Count());
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line.AsSpan(Math.Min(indent, line.Length)).TrimEnd());
        }

        return sb.ToString();
    }
}
