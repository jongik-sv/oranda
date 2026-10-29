using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.Language.Ai.Tools;

public sealed partial class TuningToolbox
{
    /// <summary>한 번에 견줄 문장 수 상한.</summary>
    public const int MaxBenchmarkVariants = 5;

    /// <summary>문장당 반복 상한.</summary>
    public const int MaxBenchmarkRepeat = 5;

    /// <summary>운영 접속에서 문장 하나(반복 전체)에 허용하는 시간. 넘으면 그 문장 실행을 취소한다.</summary>
    public static readonly TimeSpan ProductionStatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>benchmark_queries</c> 의 대체 경로: 문장을 실행하지 않고, 글에서 sql_id 를 계산해 이 실행 범위의 스키마가 파싱한 공유 풀 커서가 있으면 그 실측을 돌려준다.
    /// 운영에서 이미 돈 같은 글의 실측이므로 위험 함수를 다시 부르지 않고도 비교할 수 있다. 커서가 없거나 V$SQL 을 볼 수 없으면 계획 기준으로 판단하라고 알린다.
    /// 바인드 변수가 있는 글도 받는다(실행하지 않으므로, 앱이 실제로 돌린 글은 대개 바인드를 쓴다).
    /// </summary>
    public async Task<BenchmarkSubstitute> CursorStatsInsteadAsync(ISession s, BenchmarkVariant variant, string reason, string? scopeSchema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(variant);
        var text = SubstituteText(variant);
        var computed = SqlIdCalculator.Compute(text);
        var visible = await TryCursorVisibleAsync(s, computed, scopeSchema, ct).ConfigureAwait(false) is true;
        var stats = visible ? await GetSqlStatsAsync(s, computed, scopeSchema, ct).ConfigureAwait(false) : null;
        if (stats is not { Found: true, Executions: > 0 })
        {
            return new BenchmarkSubstitute(variant.Label, "cursor_stats", reason, $"위험 함수·구문({reason})이라 실행하지 않음. 실측 없음, 계획 기준으로 판단",
                visible ? computed : null, 0, null, null, null, null);
        }

        return new BenchmarkSubstitute(variant.Label, "cursor_stats", reason, $"위험 함수·구문({reason})이라 다시 실행하지 않고 운영 커서 실측을 씀",
            computed, stats.Executions, stats.ElapsedMsPerExec, stats.BufferGetsPerExec, stats.DiskReadsPerExec, stats.RowsPerExec);
    }

    /// <summary>대체할 변형의 글을 실행 전에 미리 확인한다. 하나라도 볼 수 없으면 다른 변형을 실행하기 전에 거부한다(실행한 결과가 버려지지 않게).</summary>
    public void CheckSubstitutes(IEnumerable<BenchmarkVariant> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        foreach (var v in variants)
        {
            SubstituteText(v);
        }
    }

    private string SubstituteText(BenchmarkVariant variant)
    {
        try
        {
            return SingleSelect(variant.Sql, allowBinds: true, "benchmark_queries").Text;
        }
        catch (AiToolException ex)
        {
            throw new AiToolException($"'{variant.Label}' 문장을 볼 수 없습니다: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>benchmark_queries</c>: 문장마다 SELECT 한 개를 반복 실행해 경과·읽기·행 수를 잰다(SELECT 아닌 문장은 서비스가 거부한다).
    /// <paramref name="benchmarkAllowed"/>(Query 이상이고 게이트가 문장을 검사했을 때만 참)가 거짓이면 실행하지 않는다.
    /// <paramref name="statementTimeout"/> 이 있으면 문장 하나(반복 전체)가 그 시간을 넘길 때 취소한다. 운영 접속은 <see cref="ProductionStatementTimeout"/> 을 준다.
    /// <paramref name="readOnly"/> 가 참이면(게이트가 사용자 함수를 확인해 통과시킨 문장) 문장마다 읽기 전용 트랜잭션 안에서 실행한다.
    /// </summary>
    public async Task<IReadOnlyList<BenchmarkDescription>> BenchmarkAsync(
        ISession s, IReadOnlyList<BenchmarkVariant> variants, int repeat, bool benchmarkAllowed, string? scopeSchema, CancellationToken ct,
        TimeSpan? statementTimeout = null, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(variants);
        if (!benchmarkAllowed)
        {
            throw new AiToolException("이 접속의 허용 수준으로는 benchmark_queries 를 실행할 수 없습니다(Query 이상이 필요합니다).");
        }

        if (variants.Count is 0 or > MaxBenchmarkVariants)
        {
            throw new AiToolException($"견줄 문장은 1~{MaxBenchmarkVariants}개여야 합니다(받은 수: {variants.Count}).");
        }

        // 하나라도 못 보내는 문장이면 아무것도 실행하기 전에 거부한다(앞 문장만 돌고 결과가 버려지지 않게).
        var texts = new List<string>(variants.Count);
        foreach (var v in variants)
        {
            try
            {
                texts.Add(SingleSelect(v.Sql, allowBinds: false, "benchmark_queries").Text);
            }
            catch (AiToolException ex)
            {
                throw new AiToolException($"'{v.Label}' 문장을 실행할 수 없습니다: {ex.Message}");
            }
        }

        var iterations = Math.Clamp(repeat, 1, MaxBenchmarkRepeat);
        var results = new List<BenchmarkDescription>(variants.Count);
        for (var i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            BenchmarkResult r;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (statementTimeout is { } timeout)
            {
                limit.CancelAfter(timeout);
            }

            try
            {
                // 출처를 Agent 로 지정해 이력·실행 정책이 에이전트 실행으로 구분하게 한다.
                r = await tuning.RunBenchmarkAsync(s, new BenchmarkCase(v.Label, v.Sql) { Source = HistorySource.Agent, ReadOnly = readOnly }, iterations, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && limit.IsCancellationRequested)
            {
                throw new AiToolException($"'{v.Label}' 문장이 제한 시간({statementTimeout!.Value.TotalSeconds:0}초)을 넘겨 중단됐습니다.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not AiToolException && !ct.IsCancellationRequested)
            {
                // 서버 오류 원문에는 변환에 실패한 값이 들어갈 수 있으므로 오류 번호만 돌려준다.
                var code = System.Text.RegularExpressions.Regex.Match(ex.Message, @"ORA-\d{5}").Value;
                throw new AiToolException($"'{v.Label}' 문장 실행이 실패했습니다" + (code.Length > 0 ? $" ({code})." : "."));
            }

            // 실행한 문장의 sql_id: 글에서 계산한 값이 공유 풀에 있고 이 실행 범위의 스키마가 파싱한 커서일 때만 알린다.
            var computed = SqlIdCalculator.Compute(texts[i]);
            // V$SQL 권한이 없으면(null) 이미 끝난 실행 결과를 버리지 않고 sql_id 만 알리지 않는다.
            var sqlId = await TryCursorVisibleAsync(s, computed, scopeSchema, ct).ConfigureAwait(false) is true ? computed : null;
            results.Add(new BenchmarkDescription(
                v.Label, r.Iterations, r.Elapsed.TotalMilliseconds, r.LogicalReads, r.PhysicalReads, r.Rows, r.PlanHashValue, sqlId));
        }

        return results;
    }
}
