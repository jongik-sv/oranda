using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>작업 로그 한 줄의 종류(화면 색). <see cref="Statement"/> 는 보낸 SQL 글이다.</summary>
public enum CopyLogKind { Info, Statement, Warning, Error }

/// <summary>
/// 실행 화면 「작업 로그」의 한 줄. 실행기가 실제로 보낸 문장·결과를 그대로 남긴다.
/// 바인드 값은 기간 경계만 글자로 남기고 나머지(변수·키)는 개수만 남긴다(<see cref="CopyLogText.Binds"/>). 행 값·마스킹 값·비밀번호는 담지 않는다.
/// </summary>
/// <param name="Table">규칙 키. 표와 상관없는 줄이면 null.</param>
public sealed record CopyLogEntry(DateTimeOffset At, string Phase, string? Table, string Text, CopyLogKind Kind = CopyLogKind.Info);

/// <summary>작업 로그 글 만들기(실행기·화면이 같이 쓴다).</summary>
public static partial class CopyLogText
{
    /// <summary>
    /// 바인드 요약. 기간 경계(<c>:B1_FROM</c>·<c>:B1_TO</c>·<c>:FROM_DT</c>·<c>:TO_DT</c>)는 값을 글자로,
    /// 나머지는 이름마다 값 개수만 보인다: 여러 값 변수(<c>:NAME_1</c>…<c>:NAME_n</c>)는 <c>:NAME = n개</c> 로 묶는다.
    /// 바인드가 없으면 빈 글자.
    /// </summary>
    public static string Binds(IReadOnlyDictionary<string, BindValue>? binds)
    {
        if (binds is null || binds.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (name, value) in binds.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (PeriodBind().IsMatch(name))
            {
                parts.Add($":{name} = {PeriodValue(value.Value)}");
                continue;
            }

            var match = ListItemBind().Match(name);
            var group = match.Success ? match.Groups[1].Value : name;
            if (counts.TryGetValue(group, out var n))
            {
                counts[group] = n + 1;
            }
            else
            {
                counts[group] = 1;
                order.Add(group);
            }
        }

        parts.AddRange(order.Select(g => $":{g} = {counts[g].ToString(CultureInfo.InvariantCulture)}개"));
        return string.Join(", ", parts);
    }

    /// <summary>문장 한 줄: 문장 글 뒤에 바인드 요약(있으면).</summary>
    public static string Statement(string side, string sql, IReadOnlyDictionary<string, BindValue>? binds = null)
    {
        var summary = Binds(binds);
        return summary.Length == 0 ? $"{side}: {sql}" : $"{side}: {sql}  [바인드 {summary}]";
    }

    /// <summary>걸린 시간: 1분 아래면 <c>0.42초</c>, 넘으면 <c>3분 05초</c>.</summary>
    public static string Elapsed(TimeSpan elapsed) =>
        elapsed < TimeSpan.FromMinutes(1)
            ? elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "초"
            : string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}분 {elapsed.Seconds:00}초");

    /// <summary>화면·복사·저장에 쓰는 한 줄: <c>HH:mm:ss [단계] 표 · 내용</c>.</summary>
    public static string Format(CopyLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sb = new StringBuilder();
        sb.Append(entry.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append(" [").Append(entry.Phase).Append("] ");
        if (!string.IsNullOrEmpty(entry.Table))
        {
            sb.Append(entry.Table).Append(" · ");
        }

        sb.Append(entry.Kind switch
        {
            CopyLogKind.Error => "오류: ",
            CopyLogKind.Warning => "알림: ",
            _ => string.Empty,
        });
        sb.Append(entry.Text);
        return sb.ToString();
    }

    private static string PeriodValue(object? value) => value switch
    {
        null => "NULL",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        string s => "'" + s + "'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "NULL",
    };

    [GeneratedRegex(@"^(B\d+_(FROM|TO)|FROM_DT|TO_DT)$", RegexOptions.CultureInvariant)]
    private static partial Regex PeriodBind();

    [GeneratedRegex(@"^(.+)_\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ListItemBind();
}
