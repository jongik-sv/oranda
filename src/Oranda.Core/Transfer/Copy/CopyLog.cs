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

    /// <summary>
    /// 멈춤 표시 글(결정 70): 대상 쓰기 = <c>배치 3 쓰는 중 · 2분째 대기</c>, 원본 읽기 = <c>원본 읽는 중 · 2분째</c>,
    /// 대상 정리 = <c>대상 정리(DELETE) 중 · 2분째 대기</c>, 테이블 커밋 = <c>테이블 커밋 중 · 2분째 대기</c>.
    /// </summary>
    public static string Wait(CopyWait wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        var minute = wait.Minute.ToString(CultureInfo.InvariantCulture);
        var text = wait.Step switch
        {
            CopyWaitStep.Delete => $"대상 정리(DELETE) 중 · {minute}분째 대기",
            CopyWaitStep.Commit => $"테이블 커밋 중 · {minute}분째 대기",
            _ => wait.Side == CopyWaitSide.Source
                ? $"원본 읽는 중 · {minute}분째"
                : $"배치 {(wait.Batch ?? 0).ToString(CultureInfo.InvariantCulture)} 쓰는 중 · {minute}분째 대기",
        };
        return WithCause(text, wait);
    }

    /// <summary>대기 글 뒤에 대기 원인(WH1, <see cref="CopyWait.Cause"/>)을 덧붙인다: <c>… · 3분째 대기 · 행 잠금(enq: TX) · 막는 세션 123 (APPUSER@was01)</c>. 없으면 그대로.</summary>
    public static string WithCause(string text, CopyWait wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        return string.IsNullOrWhiteSpace(wait.Cause) ? text : text + " · " + wait.Cause;
    }

    /// <summary>동시 복사 단계 글(PF2): <c>동시 2개 · 단계 1/3</c>.</summary>
    public static string Stage(CopyStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return $"동시 {stage.Lanes.ToString(CultureInfo.InvariantCulture)}개 · 단계 {stage.Index.ToString(CultureInfo.InvariantCulture)}/{stage.Count.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 시간 나눔 글(결정 74): <c>읽기 1.20초 · 변환 0.05초 · 쓰기 3.40초 · 1,234행/초</c>. 적재 구간·테이블 전체를 알면(원격 #155) 기준을 함께 적는다:
    /// <c>적재 구간 4.49초 · 읽기 1.20초(첫 행까지 0.30초) · 변환 0.05초 · 쓰기 3.40초 · 66,811행/초 · 테이블 전체 10.70초 · 기타 6.21초</c>
    /// (행/초 = 넣은 행 / 적재 구간, 기타 = 테이블 전체 − 적재 구간: 정리·세이브포인트·커밋·건수 비교).
    /// </summary>
    public static string Times(CopyTableTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);
        return (times.Load is { } load ? $"적재 구간 {Elapsed(load)} · " : string.Empty)
               + $"읽기 {Elapsed(times.Read)}" + (times.FirstRow is { } first ? $"(첫 행까지 {Elapsed(first)})" : string.Empty)
               + $" · 변환 {Elapsed(times.Convert)} · 쓰기 {Elapsed(times.Write)} · "
               + times.RowsPerSecond.ToString("N0", CultureInfo.InvariantCulture) + "행/초"
               + (times.Total is { } total ? $" · 테이블 전체 {Elapsed(total)}" : string.Empty)
               + (times.Other is { } other ? $" · 기타 {Elapsed(other)}" : string.Empty);
    }

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
