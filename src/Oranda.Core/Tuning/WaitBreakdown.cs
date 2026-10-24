namespace Oranda.Core.Tuning;

// SQL 별 대기 분해(TN-12·14, TSK-06-06). gv$session 표본·ASH 줄을 이벤트·대기 분류 비율로 묶는다.
// 화면(SQL 모니터)과 튜닝 보고서(TSK-06-07)가 같은 표·같은 분류 색을 쓴다.

/// <summary>대기 분류의 색 묶음. 화면은 아이콘 브러시(CPU 초록·User I/O 파랑·Application 빨강·Cluster 보라·Concurrency 호박·그 밖 회색)로 그린다.</summary>
public enum WaitTone
{
    Cpu,
    UserIo,
    Application,
    Cluster,
    Concurrency,
    Other,
}

/// <summary>이벤트 한 줄의 비율.</summary>
/// <param name="Event">event(CPU 이면 <see cref="WaitBreakdown.OnCpu"/>)</param>
/// <param name="WaitClass">wait_class(CPU 이면 <see cref="WaitBreakdown.CpuClass"/>)</param>
/// <param name="Samples">표본 수</param>
/// <param name="Share">전체 표본 대비(0~1)</param>
/// <param name="Sessions">표본에 나온 세션 수(ASH 줄은 모름 = null)</param>
public sealed record WaitEventShare(string Event, string WaitClass, long Samples, double Share, int? Sessions)
{
    public WaitTone Tone => WaitBreakdown.ToneOf(WaitClass);
}

/// <summary>대기 분류 한 묶음의 비율(색 막대 한 칸). 색이 없는 분류는 모두 「기타」 한 칸으로 모은다.</summary>
public sealed record WaitClassShare(WaitTone Tone, string Label, long Samples, double Share);

/// <summary>대기 표본을 비율로 묶는다.</summary>
public static class WaitBreakdown
{
    /// <summary>CPU 위에서 돈 표본의 이벤트 이름.</summary>
    public const string OnCpu = "ON CPU";

    /// <summary>CPU 표본의 분류 이름.</summary>
    public const string CpuClass = "CPU";

    /// <summary>색이 없는 분류를 모은 칸 이름.</summary>
    public const string OtherLabel = "기타";

    /// <summary>gv$session 표본(대기 아님 = CPU)을 이벤트별로 센다. 많은 순, 같으면 이벤트 이름 순.</summary>
    public static IReadOnlyList<WaitEventShare> FromSamples(IEnumerable<WaitSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var groups = samples
            .GroupBy(s => s.Event is null ? (OnCpu, CpuClass) : (s.Event, s.WaitClass ?? "Other"))
            .Select(g => (Key: g.Key, Count: (long)g.Count(), Sessions: g.Select(s => s.Sid).Distinct().Count()))
            .ToList();
        var total = groups.Sum(g => g.Count);
        return [.. groups
            .OrderByDescending(g => g.Count).ThenBy(g => g.Key.Item1, StringComparer.Ordinal)
            .Select(g => new WaitEventShare(g.Key.Item1, g.Key.Item2, g.Count, total > 0 ? (double)g.Count / total : 0, g.Sessions))];
    }

    /// <summary>ASH 줄(이미 이벤트로 묶임)을 같은 모양으로. 비율은 다시 계산한다.</summary>
    public static IReadOnlyList<WaitEventShare> FromAsh(IEnumerable<AshWaitRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        var total = list.Sum(r => r.Samples);
        return [.. list
            .OrderByDescending(r => r.Samples).ThenBy(r => r.Event, StringComparer.Ordinal)
            .Select(r =>
            {
                var cpu = string.Equals(r.Event, OnCpu, StringComparison.OrdinalIgnoreCase);
                return new WaitEventShare(cpu ? OnCpu : r.Event, cpu ? CpuClass : r.WaitClass ?? "Other", r.Samples, total > 0 ? (double)r.Samples / total : 0, null);
            })];
    }

    /// <summary>분류별 합(색 막대). 많은 순이고 「기타」는 맨 끝.</summary>
    public static IReadOnlyList<WaitClassShare> Classes(IReadOnlyList<WaitEventShare> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var total = events.Sum(e => e.Samples);
        return [.. events
            .GroupBy(e => e.Tone)
            .Select(g => new WaitClassShare(g.Key, g.Key == WaitTone.Other ? OtherLabel : g.First().WaitClass, g.Sum(e => e.Samples), 0))
            .OrderBy(c => c.Tone == WaitTone.Other ? 1 : 0).ThenByDescending(c => c.Samples).ThenBy(c => c.Tone)
            .Select(c => c with { Share = total > 0 ? (double)c.Samples / total : 0 })];
    }

    /// <summary>wait_class → 색 묶음. 대소문자는 가리지 않는다.</summary>
    public static WaitTone ToneOf(string? waitClass) => waitClass?.Trim().ToUpperInvariant() switch
    {
        "CPU" => WaitTone.Cpu,
        "USER I/O" => WaitTone.UserIo,
        "APPLICATION" => WaitTone.Application,
        "CLUSTER" => WaitTone.Cluster,
        "CONCURRENCY" => WaitTone.Concurrency,
        _ => WaitTone.Other,
    };
}
