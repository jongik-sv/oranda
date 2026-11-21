using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>테이블 하나의 수집 상태.</summary>
public enum GatherStatsOutcome
{
    Waiting,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>테이블 하나의 수집 결과(걸린 시간·한 줄 글).</summary>
public sealed record GatherStatsResult(TableStatsTarget Target, GatherStatsOutcome Outcome, TimeSpan Elapsed, string Message);

/// <summary>
/// 통계 수집 실행(SG-1): 테이블마다 <c>DBMS_STATS.GATHER_TABLE_STATS</c> 블록 하나를 차례로 보낸다. 하나가 실패해도 다음 테이블로 넘어가고,
/// 취소하면 지금 테이블을 멈추고(드라이버 cancel) 나머지는 「취소」로 둔다. 실행은 <see cref="IExecutionService"/> 한 길로 — 이력에 남고
/// 확인 창에서 이미 받은 확인은 <c>PreConfirmed</c> 로 넘겨 다시 묻지 않는다(읽기 전용 접속 거부는 그대로).
/// </summary>
public sealed class GatherStatsRunner
{
    /// <summary>실행 이력 출처.</summary>
    public static HistorySource Source => HistorySource.SchemaBrowser;

    private readonly IExecutionService _exec;

    public GatherStatsRunner(IExecutionService exec) => _exec = exec ?? throw new ArgumentNullException(nameof(exec));

    /// <summary>실행할 문장(가짜 세션 시험·미리보기와 같은 글).</summary>
    public static SqlStatement Statement(string sql) => new(sql, default, StatementKind.PlSql) { Verb = "BEGIN" };

    /// <param name="progress">테이블 순번과 그 상태(실행 중·끝)를 알린다.</param>
    public async Task<IReadOnlyList<GatherStatsResult>> RunAsync(ISession session, IReadOnlyList<TableStatsTarget> targets, GatherStatsOptions options,
        Action<int, GatherStatsResult>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);
        var dialect = GatherStatsDialect.For(session.Profile.Dbms);
        var results = new List<GatherStatsResult>(targets.Count);
        var cancelled = false;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (cancelled || ct.IsCancellationRequested)
            {
                cancelled = true;
                Report(i, new GatherStatsResult(target, GatherStatsOutcome.Cancelled, TimeSpan.Zero, T("취소했습니다.", "Cancelled.")));
                continue;
            }

            string sql;
            try
            {
                sql = dialect.Build(target.Owner, target.Table, options);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                Report(i, new GatherStatsResult(target, GatherStatsOutcome.Failed, TimeSpan.Zero, ex.Message));
                continue;
            }

            progress?.Invoke(i, new GatherStatsResult(target, GatherStatsOutcome.Running, TimeSpan.Zero, T("수집하는 중…", "Gathering…")));
            var stmt = Statement(sql);
            var exec = new ExecutionOptions { Source = Source, PreConfirmed = new HashSet<string>(StringComparer.Ordinal) { sql } };
            var watch = Stopwatch.StartNew();
            GatherStatsResult result;
            try
            {
                var r = await _exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(), exec, ct).ConfigureAwait(true);
                var elapsed = r.Elapsed > TimeSpan.Zero ? r.Elapsed : watch.Elapsed;
                result = r.Status switch
                {
                    ExecutionStatus.Succeeded => new GatherStatsResult(target, GatherStatsOutcome.Succeeded, elapsed, T("수집했습니다.", "Gathered.")),
                    ExecutionStatus.Cancelled => new GatherStatsResult(target, GatherStatsOutcome.Cancelled, elapsed, T("취소했습니다.", "Cancelled.")),
                    _ when r.Error is { Kind: DbErrorKind.Cancelled } => new GatherStatsResult(target, GatherStatsOutcome.Cancelled, elapsed, T("취소했습니다.", "Cancelled.")),
                    _ => new GatherStatsResult(target, GatherStatsOutcome.Failed, elapsed,
                        r.Error is { } e ? Describe(e, target) : r.Policy?.Message ?? r.Status.ToString()),
                };
            }
            catch (OperationCanceledException)
            {
                result = new GatherStatsResult(target, GatherStatsOutcome.Cancelled, watch.Elapsed, T("취소했습니다.", "Cancelled."));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new GatherStatsResult(target, GatherStatsOutcome.Failed, watch.Elapsed, FirstLine(ex.Message));
            }

            cancelled = result.Outcome == GatherStatsOutcome.Cancelled;
            Report(i, result);
        }

        return results;

        void Report(int index, GatherStatsResult result)
        {
            results.Add(result);
            progress?.Invoke(index, result);
        }
    }

    /// <summary>오류를 한 줄로. 권한 없음(ORA-20000·01031·00942 등)은 무엇을 받아야 하는지 알기 쉽게.</summary>
    public static string Describe(DbError error, TableStatsTarget target)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(target);
        var first = FirstLine(error.Message);
        if (error.Kind.IsNoAccess() || error.Number == 20000 || error.Code == "ORA-20000")
        {
            return T($"권한이 없어 {target.Display} 의 통계를 모으지 못했습니다. 소유자로 접속하거나 ANALYZE ANY 권한을 받으세요. ({first})",
                $"No privilege to gather statistics on {target.Display}. Connect as the owner or get the ANALYZE ANY privilege. ({first})");
        }

        return first;
    }
}
