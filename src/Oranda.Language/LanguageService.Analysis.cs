using System.Collections.Concurrent;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Language.Lexing;
using Oranda.Language.Parsing;

namespace Oranda.Language;

/// <summary>
/// L2 ANTLR 분석(TRD 4.5). 문서를 L1 로 문장 단위로 나누고 <b>바뀐 문장만</b> 다시 파싱한다(문장 원문 → 결과 캐시).
/// 항상 스레드 풀에서 돌고(UI 스레드를 막지 않는다) 취소할 수 있다. 300 ms 디바운스는 부르는 쪽(편집기) 몫이다. 소유: TSK-04-03.
/// </summary>
internal sealed partial class LanguageService
{
    /// <summary>캐시 상한. 넘으면 비운다(문서 몇 개 분량이면 충분하다).</summary>
    private const int AnalysisCacheLimit = 4096;

    private readonly ConcurrentDictionary<(string Sql, StatementKind Kind), StatementAnalysis> _analysisCache = new();
    private long _parsedStatements;

    /// <summary>캐시에 없어 실제로 파싱한 문장 수(시험·진단용).</summary>
    internal long ParsedStatementCount => Interlocked.Read(ref _parsedStatements);

    public Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.Run<IReadOnlyList<Diagnostic>>(() => Analyze(text, ct), ct);
    }

    private List<Diagnostic> Analyze(string text, CancellationToken ct)
    {
        var result = new List<Diagnostic>();
        foreach (var statement in StatementSplitter.Split(text, SplitOptions.Default))
        {
            ct.ThrowIfCancellationRequested();
            var range = statement.Range;
            if (statement.Kind == StatementKind.SqlPlus || range.Length <= 0 || range.End > text.Length)
            {
                continue;
            }

            var key = (text.Substring(range.Start, range.Length), statement.Kind);
            if (!_analysisCache.TryGetValue(key, out var analysis))
            {
                analysis = PlSqlAnalyzer.Analyze(key.Item1, statement.Kind, ct);
                Interlocked.Increment(ref _parsedStatements);
                if (_analysisCache.Count >= AnalysisCacheLimit)
                {
                    _analysisCache.Clear();
                }

                _analysisCache[key] = analysis;
            }

            foreach (var d in analysis.Diagnostics)
            {
                result.Add(d with { Range = new TextRange(range.Start + d.Range.Start, d.Range.Length) });
            }
        }

        return result;
    }
}
