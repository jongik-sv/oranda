using System.Collections.Concurrent;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Language.Lexing;
using Oranda.Language.Parsing;

namespace Oranda.Language;

/// <summary>
/// L2 ANTLR 분석(TRD 4.5). 문서를 L1 로 문장 단위로 나누고 <b>바뀐 문장만</b> 다시 파싱한다(문장 원문 → 결과 캐시).
/// 바뀌지 않은 문장은 캐시 결과를 새 문장 위치로 옮겨 쓴다. L1 분리는 매번 문서 전체를 훑는다(1만 줄 수 ms, TSK-04-09).
/// 항상 스레드 풀에서 돌고(UI 스레드를 막지 않는다) 취소할 수 있다. 디바운스는 부르는 쪽(편집기) 몫이다. 소유: TSK-04-03.
/// </summary>
internal sealed partial class LanguageService
{
    /// <summary>
    /// 캐시 상한(문장 수). 1만 줄 문서 하나가 문장 7,500~10,000개라 큰 문서 탭 2개 분량을 담는다.
    /// 넘으면 분석이 끝날 때 오래 안 쓴 문장부터 버리되 이번·직전 분석이 쓴 문장은 남긴다(<see cref="Evict"/>).
    /// 예전에는 4,096 에서 분석 도중 통째로 비워, 그보다 문장이 많은 문서는 편집할 때마다 전체를 다시 파싱했다(TSK-04-09).
    /// </summary>
    internal const int AnalysisCacheLimit = 16_384;

    private readonly ConcurrentDictionary<(string Sql, StatementKind Kind), CacheEntry> _analysisCache = new();
    private readonly object _evictGate = new();
    private long _parsedStatements;
    private long _pass;

    /// <summary>캐시에 없어 실제로 파싱한 문장 수(시험·진단용).</summary>
    internal long ParsedStatementCount => Interlocked.Read(ref _parsedStatements);

    /// <summary>캐시에 든 문장 수(시험·진단용).</summary>
    internal int CachedStatementCount => _analysisCache.Count;

    /// <summary>캐시 상한. 시험이 작게 줄 때만 바꾼다.</summary>
    internal int CacheLimit { get; init; } = AnalysisCacheLimit;

    public Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.Run<IReadOnlyList<Diagnostic>>(() => Analyze(text, ct), ct);
    }

    private List<Diagnostic> Analyze(string text, CancellationToken ct)
    {
        long pass = Interlocked.Increment(ref _pass);
        var result = new List<Diagnostic>();
        foreach (var statement in StatementSplitter.Split(text, SplitOptions.Default))
        {
            ct.ThrowIfCancellationRequested();
            var range = statement.Range;
            if (statement.Kind == StatementKind.SqlPlus || range.Length <= 0 || range.End > text.Length)
            {
                continue;
            }

            // SQL*Plus 가 아닌 문장의 Text 는 문서에서 Range 만큼 잘라 낸 원문이다(StatementClassifier.Build).
            var key = (statement.Text, statement.Kind);
            if (_analysisCache.TryGetValue(key, out var entry))
            {
                entry.Touch(pass);
            }
            else
            {
                entry = new CacheEntry(PlSqlAnalyzer.Analyze(statement.Text, statement.Kind, ct), pass);
                Interlocked.Increment(ref _parsedStatements);
                _analysisCache[key] = entry;
            }

            foreach (var d in entry.Analysis.Diagnostics)
            {
                result.Add(d with { Range = new TextRange(range.Start + d.Range.Start, d.Range.Length) });
            }
        }

        if (_analysisCache.Count > CacheLimit)
        {
            Evict(pass);
        }

        return result;
    }

    /// <summary>
    /// 상한의 3/4 까지 오래 안 쓴 문장부터 버린다. 이번·직전 분석이 쓴 문장은 버리지 않는다
    /// (블록 주석을 열었다 닫으면 직전 분석의 문장이 그대로 돌아온다). 그래서 문서 하나가 상한보다 크면 상한을 넘어 남을 수 있다.
    /// </summary>
    private void Evict(long pass)
    {
        lock (_evictGate)
        {
            int excess = _analysisCache.Count - (CacheLimit / 4 * 3);
            if (excess <= 0)
            {
                return;
            }

            var victims = _analysisCache
                .Where(p => p.Value.LastPass < pass - 1)
                .OrderBy(p => p.Value.LastPass)
                .Take(excess)
                .Select(p => p.Key)
                .ToList();
            foreach (var key in victims)
            {
                _analysisCache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>캐시 항목: 분석 결과와 마지막으로 쓴 분석 회차.</summary>
    private sealed class CacheEntry(StatementAnalysis analysis, long pass)
    {
        private long _lastPass = pass;

        public StatementAnalysis Analysis { get; } = analysis;

        public long LastPass => Interlocked.Read(ref _lastPass);

        public void Touch(long pass)
        {
            // 동시에 도는 분석(취소된 이전 분석, 다른 탭)이 더 작은 회차로 덮지 않게 한다.
            long seen = LastPass;
            while (seen < pass)
            {
                long previous = Interlocked.CompareExchange(ref _lastPass, pass, seen);
                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
