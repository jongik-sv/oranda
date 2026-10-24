using Oranda.Language.Lexing;

namespace Oranda.Language.Tuning;

/// <summary>벤치마크 변형 하나의 측정 요약(판정 입력).</summary>
/// <param name="Elapsed">반복 평균 경과. 끝나지 않았으면 null.</param>
/// <param name="LogicalReads">반복 평균 논리 읽기.</param>
/// <param name="Rows">가져온 행 수.</param>
public sealed record BenchmarkMeasure(TimeSpan? Elapsed, long LogicalReads, long? Rows);

/// <summary>변형 하나의 판정.</summary>
public enum BenchmarkVerdictKind
{
    /// <summary>원문(비교 기준).</summary>
    Baseline,

    /// <summary>행 수가 원문과 같고 원문보다 빠른 것 중 가장 나음.</summary>
    Best,

    Faster,

    Slower,

    /// <summary>행 수가 원문과 다르다(판정에서 뺀다).</summary>
    RowMismatch,

    /// <summary>재지 못했다(실패·취소·거부·아직).</summary>
    NotMeasured,
}

/// <summary>판정 한 줄. <see cref="Ratio"/> = 원문 경과 / 이 경과(2.0 이면 두 배 빠름). <see cref="RowDelta"/> = 이 행 수 − 원문 행 수.</summary>
public sealed record BenchmarkVerdict(BenchmarkVerdictKind Kind, double? Ratio, long? RowDelta);

/// <summary>
/// 변형 SQL 벤치마크 규칙(TSK-06-05, TN-10). 순수 함수: 실행해도 되는 문장인지(SELECT·WITH … SELECT 만), 결과를 어떻게 판정하는지.
/// 「가장 나음」 = 행 수가 원문과 같은 변형 중 경과 평균이 가장 작고 원문보다 빠른 것, 같으면 논리 읽기가 적은 쪽.
/// </summary>
public static class BenchmarkRules
{
    /// <summary>반복 수 범위.</summary>
    public const int MinIterations = 1;

    public const int MaxIterations = 20;

    public const int DefaultIterations = 3;

    /// <summary>
    /// 벤치마크하면 안 되는 이유. 해도 되면 null. SELECT·WITH … SELECT 만 받고, FOR UPDATE·바인드·치환 변수·여러 문장·WITH FUNCTION 은 거부한다.
    /// </summary>
    public static string? RejectReason(string? sql, bool english = false)
    {
        var text = Trim(sql);
        if (text.Length == 0)
        {
            return english ? "The statement is empty." : "문장이 비었습니다.";
        }

        var t = new TuningSql(text);
        if (t.Count == 0)
        {
            return english ? "The statement is empty." : "문장이 비었습니다.";
        }

        if (Enumerable.Range(0, t.Count).Any(k => t.IsPunct(k, ';')))
        {
            return english ? "Only one statement can be benchmarked." : "문장 하나만 벤치마크합니다.";
        }

        var first = 0;
        while (first < t.Count && t.IsPunct(first, '('))
        {
            first++;
        }

        if (t.Is(first, "WITH") && (t.Is(first + 1, "FUNCTION") || t.Is(first + 1, "PROCEDURE")))
        {
            return english ? "WITH FUNCTION (inline PL/SQL) is not benchmarked." : "WITH FUNCTION(인라인 PL/SQL)은 벤치마크하지 않습니다.";
        }

        if (t.MainSelect() < 0)
        {
            var verb = Verb(t);
            return english
                ? $"Only SELECT statements are benchmarked{(verb is null ? string.Empty : " (this one is " + verb + ")")}."
                : $"SELECT 문만 벤치마크합니다{(verb is null ? string.Empty : "(이 문장: " + verb + ")")}.";
        }

        if (Enumerable.Range(0, t.Count - 1).Any(k => t.Is(k, "FOR") && t.Is(k + 1, "UPDATE")))
        {
            return english ? "FOR UPDATE locks rows, so it is not benchmarked." : "FOR UPDATE 는 행을 잠가서 벤치마크하지 않습니다.";
        }

        var bind = t.Tokens.FirstOrDefault(x => x.Kind is TokenKind.Bind or TokenKind.Substitution);
        if (bind.Length > 0)
        {
            var name = text.Substring(bind.Start, bind.Length);
            return english
                ? $"It has the variable {name}. Benchmark a copy with the value written in."
                : $"변수 {name} 가 있습니다. 값을 써 넣은 사본으로 벤치마크하세요.";
        }

        return null;
    }

    /// <summary>앞뒤 공백·끝 <c>;</c>·<c>/</c> 를 뺀 실행 글.</summary>
    public static string Trim(string? sql)
    {
        var text = (sql ?? string.Empty).Trim();
        while (text.EndsWith(';') || text.EndsWith('/'))
        {
            text = text[..^1].TrimEnd();
        }

        return text;
    }

    /// <summary>
    /// 판정. <paramref name="measures"/>[0] = 원문. 원문을 재지 못했으면 모두 <see cref="BenchmarkVerdictKind.NotMeasured"/>(원문 자신은 Baseline).
    /// </summary>
    public static IReadOnlyList<BenchmarkVerdict> Judge(IReadOnlyList<BenchmarkMeasure> measures)
    {
        ArgumentNullException.ThrowIfNull(measures);
        var result = new List<BenchmarkVerdict>(measures.Count);
        if (measures.Count == 0)
        {
            return result;
        }

        var baseline = measures[0];
        result.Add(new BenchmarkVerdict(BenchmarkVerdictKind.Baseline, baseline.Elapsed is null ? null : 1, 0));
        var best = -1;
        for (var i = 1; i < measures.Count; i++)
        {
            var m = measures[i];
            if (m.Elapsed is not { } e || baseline.Elapsed is not { } be)
            {
                result.Add(new BenchmarkVerdict(BenchmarkVerdictKind.NotMeasured, null, null));
                continue;
            }

            var ratio = e.Ticks > 0 ? be.TotalMilliseconds / e.TotalMilliseconds : (double?)null;
            if (m.Rows != baseline.Rows)
            {
                result.Add(new BenchmarkVerdict(BenchmarkVerdictKind.RowMismatch, ratio, m.Rows - baseline.Rows));
                continue;
            }

            var faster = e < be || (e == be && m.LogicalReads < baseline.LogicalReads);
            result.Add(new BenchmarkVerdict(faster ? BenchmarkVerdictKind.Faster : BenchmarkVerdictKind.Slower, ratio, 0));
            if (faster && (best < 0 || e < measures[best].Elapsed!.Value || (e == measures[best].Elapsed!.Value && m.LogicalReads < measures[best].LogicalReads)))
            {
                best = i;
            }
        }

        if (best > 0)
        {
            result[best] = result[best] with { Kind = BenchmarkVerdictKind.Best };
        }

        return result;
    }

    private static string? Verb(TuningSql t)
    {
        for (var k = 0; k < t.Count; k++)
        {
            if (t.Kind(k) is TokenKind.Keyword or TokenKind.Identifier)
            {
                return t.Word(k).ToUpperInvariant();
            }
        }

        return null;
    }
}
