using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// 복사 실행기(설계 8.2). 순서: 끌 트리거·FK 기록 → 끄기 → 대상 정리(자식 → 부모 DELETE) → 적재(부모 → 자식, 배치 바인드 배열)
/// → 시퀀스 보정 → 대상 트랜잭션 끝내기 → 다시 켜기 → 건수 비교.
/// <para>
/// 원본은 읽기 전용 프로필의 전용 세션이다. 시작할 때 바인드 없는 ROLLBACK 으로 앞 트랜잭션을 닫고, 「같은 시점 기준 읽기」면
/// <c>SET TRANSACTION READ ONLY</c> 를 연다. 원본 읽기는 모두 <see cref="IBulkExecutionService.StreamRowsAsync"/> 다(원본에 <see cref="IExecutionService"/> 를 쓰지 않는다).
/// </para>
/// <para>
/// ALTER 는 DDL 이라 묵시 커밋한다. 그래서 다시 켜기 전에 대상 트랜잭션을 반드시 명시적으로 끝낸다:
/// 중지·예외 = ROLLBACK, 「모두 끝난 뒤 한 번」 = 실패 표가 있으면 ROLLBACK 아니면 COMMIT, 나머지 = COMMIT.
/// 중지 뒤 정리(롤백·다시 켜기·기록)는 <c>ct</c> 대신 새 토큰을 쓴다: 실행 엔진은 취소된 토큰을 받으면 문장을 보내지 않는다.
/// </para>
/// </summary>
[OrandaService(typeof(ICopyRunner))]
internal sealed class CopyRunner : ICopyRunner
{
    /// <summary>중지 뒤 정리에 주는 시간.</summary>
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(2);

    /// <summary>오래 걸릴 수 있는 정리(큰 적재의 ROLLBACK, FK ENABLE VALIDATE, 건수 비교)에 주는 시간.</summary>
    internal static readonly TimeSpan LongCleanupTimeout = TimeSpan.FromMinutes(30);

    internal const string PhaseDisable = "끄기";
    internal const string PhaseDelete = "정리";
    internal const string PhaseLoad = "적재";
    internal const string PhaseSequence = "시퀀스";
    internal const string PhaseEnable = "다시 켜기";
    internal const string PhaseCount = "건수 비교";
    internal const string PhaseRetry = "다시 넣기";

    private const int SnapshotTooOld = 1555;

    /// <summary>시퀀스 증가값·디스크에 적힌 상한(LAST_NUMBER)·캐시 크기. 시퀀스 SELECT 권한이 있으면 ALL_SEQUENCES 에 보인다.</summary>
    private const string SequenceIncrementSql =
        "SELECT increment_by, last_number, cache_size FROM all_sequences WHERE sequence_owner = :OWNER AND sequence_name = :NAME";

    /// <summary>FK 의 자식 열·부모 열(위치 순서)과 부모 표. 부모 없는 자식 행 수를 셀 때 쓴다.</summary>
    private const string ForeignKeyColumnsSql =
        "SELECT cc.column_name, pc.column_name, p.owner, p.table_name"
        + " FROM all_constraints c"
        + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
        + " JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
        + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
        + " WHERE c.owner = :OWNER AND c.constraint_name = :NAME"
        + " ORDER BY cc.position";

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private static readonly Regex QuotedValue = new("'[^']*'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex ColumnValues = new(@"column values \([^)]*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private readonly IBulkExecutionService _bulk;
    private readonly IExecutionService _execution;
    private readonly ICopyRunLog _runLog;

    public CopyRunner(IBulkExecutionService bulk, IExecutionService execution, ICopyRunLog runLog)
    {
        _bulk = bulk;
        _execution = execution;
        _runLog = runLog;
    }

    public async Task<CopyRunResult> RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions,
                                              ResolvedPeriod period, IProgress<CopyProgress>? progress, CopyRunControl control,
                                              CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(control);
        EnsureSessions(sessions);
        if (report.HasErrors)
        {
            throw new InvalidOperationException("사전 점검에 오류가 있어 실행할 수 없습니다.");
        }

        EnsureReportMatchesJob(job, report);
        EnsureDistinct(sessions, job);
        var run = new RunState(sessions, job, report, period, decisions.PreConfirmedSql, progress);
        await using var record = await _runLog.BeginAsync(
            new CopyRunHeader(job.Id, job.Name, job.Source.Name, job.Target.Name, period, DateTimeOffset.Now), ct);
        run.Record = record;

        var finished = false;
        try
        {
            await OpenSourceAsync(run, ct);
            if (!run.Aborted)
            {
                await DisableAsync(run, decisions, ct);
            }

            if (!run.Aborted)
            {
                await DeleteTargetsAsync(run, ct);
                await LoadAllAsync(run, control, ct);
            }

            if (!run.Aborted)
            {
                await AdvanceSequencesAsync(run, decisions.ProceedSequenceGap, ct);
            }

            finished = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Stopped = true;
            finished = true;
        }
        finally
        {
            // finished 가 거짓이면 예상 못 한 예외가 올라가는 중이다: 되돌리고 다시 켠 뒤 예외를 그대로 올린다.
            await FinishAsync(run, faulted: !finished);
        }

        return run.Result();
    }

    public async Task<CopyRunResult> RetryFailedRowsAsync(CopySessions sessions, CopyJob job, PrecheckReport report,
                                                          IReadOnlyList<FailedRow> rows, IProgress<CopyProgress>? progress,
                                                          CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(rows);
        EnsureSessions(sessions);

        // 기간 경계는 키로 다시 읽을 때 쓰지 않는다(키 조건만). 실행 기록 머리글에는 작업의 기간 규칙 이름만 남는다.
        EnsureReportMatchesJob(job, report);
        EnsureDistinct(sessions, job);
        var period = new ResolvedPeriod(null, null);
        var run = new RunState(sessions, job, report, period, new HashSet<string>(StringComparer.Ordinal), progress);
        run.IsRetry = true;
        run.RetryInputs = rows;
        await using var record = await _runLog.BeginAsync(
            new CopyRunHeader(job.Id, job.Name, job.Source.Name, job.Target.Name, period, DateTimeOffset.Now), ct);
        run.Record = record;

        var finished = false;
        try
        {
            await OpenSourceAsync(run, ct);
            if (!run.Aborted)
            {
                foreach (var group in rows.GroupBy(r => r.Table, StringComparer.Ordinal))
                {
                    await RetryTableAsync(run, group.Key, [.. group], ct);
                }
            }

            finished = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Stopped = true;
            finished = true;
        }
        finally
        {
            await FinishAsync(run, faulted: !finished);
        }

        return run.Result();
    }

    // ---- 시작 ----

    /// <summary>
    /// 사전 점검 뒤 규칙이 바뀌었으면 실행하지 않는다. 문장(DELETE·SELECT)은 작업에서, MERGE 판정·마스킹·끄기는 보고서에서 만들므로
    /// 둘이 다르면 미리 받은 확인(PreConfirmedSql)이 다른 뜻의 문장에 쓰이거나 제외한 표를 읽을 수 있다.
    /// </summary>
    private static void EnsureReportMatchesJob(CopyJob job, PrecheckReport report)
    {
        if (job.Tables.Any(r => r.Kind != RuleKind.Exclude && r.Action == TargetAction.Recreate))
        {
            // T10 이 지우고 다시 만드는 단계를 더하기 전까지는 일반 INSERT 로 처리되어 중복·PK 충돌이 난다.
            throw new InvalidOperationException("「테이블 다시 만들기」 대상 처리는 아직 실행할 수 없습니다.");
        }

        if (report.Order.Any(p => !string.Equals(p.Source.Table.Owner, job.SourceOwner, StringComparison.Ordinal)
                                  || !string.Equals(p.Target.Table.Owner, job.TargetOwner, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("사전 점검 뒤 원본·대상 소유자가 바뀌었습니다. 사전 점검을 다시 하세요.");
        }

        var active = job.Tables.Where(r => r.Kind != RuleKind.Exclude).ToDictionary(r => r.Table, StringComparer.Ordinal);
        var planned = report.Order.Select(p => p.Rule.Table).ToHashSet(StringComparer.Ordinal);
        var same = active.Count == planned.Count
                   && report.Order.All(p => active.TryGetValue(p.Rule.Table, out var rule) && SameRule(rule, p.Rule));
        if (!same)
        {
            throw new InvalidOperationException("사전 점검 뒤 작업 규칙이 바뀌었습니다. 사전 점검을 다시 하세요.");
        }
    }

    /// <summary>규칙 값 비교(목록은 내용으로 비교한다. record 의 기본 비교는 목록을 참조로 비교한다).</summary>
    internal static bool SameRule(TableRule a, TableRule b) =>
        a.Table == b.Table && a.Kind == b.Kind && a.Action == b.Action
        && string.Equals(a.CustomWhere, b.CustomWhere, StringComparison.Ordinal)
        && a.PeriodColumns.SequenceEqual(b.PeriodColumns)
        && a.Masks.SequenceEqual(b.Masks)
        && a.SkipColumns.SequenceEqual(b.SkipColumns, StringComparer.Ordinal)
        && (a.Parent is null) == (b.Parent is null)
        && (a.Parent is null || (a.Parent.ParentTable == b.Parent!.ParentTable
                                 && a.Parent.ChildColumns.SequenceEqual(b.Parent.ChildColumns, StringComparer.Ordinal)
                                 && a.Parent.ParentColumns.SequenceEqual(b.Parent.ParentColumns, StringComparer.Ordinal)
                                 && a.Parent.ForeignKeyName == b.Parent.ForeignKeyName));

    /// <summary>
    /// 원본과 대상이 같은 DB·같은 소유자면 거부한다. 다른 프로필 둘이 같은 곳을 가리키면 대상 DELETE·커밋이 원본 행을 지운다.
    /// <see cref="ISession.DbKey"/> 는 <c>{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}</c> 다(앞 두 마디가 같으면 같은 DB).
    /// </summary>
    /// <remarks>
    /// 이름만으로는 복제 DB·기본 이름 컨테이너(FREE/FREEPDB1 등)를 같은 DB 로 오판한다. 그래서 호스트·인스턴스 이름도 본다.
    /// RAC 는 노드마다 호스트·인스턴스가 달라 DB 이름만 본다. 호스트·인스턴스를 모르면 같다고 본다(막는 쪽).
    /// </remarks>
    internal static bool IsSameDatabaseAndOwner(string sourceDbKey, ServerInfo source, string targetDbKey, ServerInfo target,
                                                string sourceOwner, string targetOwner)
    {
        static string Db(string key)
        {
            var last = key.LastIndexOf('/');
            return last < 0 ? key : key[..last];
        }

        static bool SameOrUnknown(string? a, string? b) =>
            a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(sourceOwner, targetOwner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Db(sourceDbKey), Db(targetDbKey), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return source.IsRac || target.IsRac
               || (SameOrUnknown(source.HostName, target.HostName) && SameOrUnknown(source.InstanceName, target.InstanceName));
    }

    private static void EnsureDistinct(CopySessions sessions, CopyJob job)
    {
        if (IsSameDatabaseAndOwner(sessions.Source.DbKey, sessions.Source.Server, sessions.Target.DbKey, sessions.Target.Server,
                                   job.SourceOwner, job.TargetOwner))
        {
            throw new InvalidOperationException("원본과 대상이 같은 DB 의 같은 소유자입니다. 원본 행을 지울 수 있어 실행하지 않습니다.");
        }
    }

    private static void EnsureSessions(CopySessions sessions)
    {
        if (sessions.Target.HasPendingTransaction)
        {
            // 첫 DDL(끄기)이나 COMMIT 이 남의 미커밋 작업을 확정하지 않게 한다. 복사는 전용 세션을 새로 연다(설계 8.1).
            throw new InvalidOperationException("대상 세션에 커밋하지 않은 작업이 있습니다. 복사용 전용 세션을 새로 여세요.");
        }

        CopyTargetGuard.EnsureAllowedTarget(sessions.Target.Profile);
        if (!sessions.Source.Profile.ReadOnly)
        {
            throw new InvalidOperationException("원본 세션은 읽기 전용 프로필(profile with { ReadOnly = true })로 열어야 합니다.");
        }
    }

    /// <summary>원본 앞 트랜잭션을 닫고 읽기 전용 트랜잭션을 연다. 그 시각이 <c>ReadStartedAt</c> 이다.</summary>
    private async Task OpenSourceAsync(RunState run, CancellationToken ct)
    {
        var rollback = await ControlAsync(run.Source, CopyStatements.Rollback(), ct);
        if (rollback.Status != ExecutionStatus.Succeeded)
        {
            run.Abort("원본 세션의 트랜잭션을 정리하지 못했습니다: " + Describe(rollback));
            return;
        }

        // 「같은 시점 기준 읽기」를 꺼도 원본에는 DB 수준 READ ONLY 를 건다(그때는 표마다 새로 연다, BeginTableReadAsync).
        run.ReadStartedAt = DateTimeOffset.Now;
        var set = await ControlAsync(run.Source, CopyStatements.SetTransactionReadOnly(), ct);
        if (set.Status != ExecutionStatus.Succeeded)
        {
            run.Abort("원본에서 읽기 전용 트랜잭션을 시작하지 못했습니다: " + Describe(set));
        }
    }

    // ---- 1. 끄기 ----

    /// <summary>끌 목록을 먼저 기록(디스크)한 뒤 끈다(Review Focus 3). 하나라도 끄지 못하면 더 진행하지 않는다.</summary>
    private async Task DisableAsync(RunState run, CopyDecisions decisions, CancellationToken ct)
    {
        var targets = new List<DisabledObject>();
        foreach (var plan in run.Report.Order)
        {
            if (plan.CanDisableTriggers && decisions.DisableTriggerTables.Contains(plan.Rule.Table))
            {
                targets.AddRange(plan.Triggers.Select(t => new DisabledObject(DisabledKind.Trigger, run.Job.TargetOwner, plan.Rule.Table, t)));
            }
        }

        var seen = new HashSet<(string, string, string)>();
        foreach (var fk in run.Report.Order.SelectMany(p => p.DisablableForeignKeys))
        {
            if (decisions.DisableForeignKeys.Contains(fk.Name) && seen.Add((fk.Owner, fk.Table, fk.Name)))
            {
                targets.Add(fk);
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        await run.Record!.RecordDisabledAsync(targets, ct);
        foreach (var obj in targets)
        {
            run.Progress(obj.Table, 0, null, PhaseDisable);
            var stmt = obj.Kind == DisabledKind.Trigger
                ? CopyStatements.DisableTrigger(obj.Owner, obj.Name)
                : CopyStatements.DisableConstraint(obj);
            // 보내기 전에 목록에 넣는다: ALTER 가 도는 중에 중지되면 서버에서 꺼졌는지 알 수 없다. 꺼지지 않은 것을 켜는 것은 해가 없다.
            run.Disabled.Add(obj);
            var r = await ExecuteLoggedAsync(run, run.Target, stmt, NoBinds, ct);
            if (r.Status != ExecutionStatus.Succeeded)
            {
                // 확정적으로 끄지 못했다(거부·실패): 다시 켤 것도, 「다시 켜기」로 보일 것도 아니다.
                run.Disabled.Remove(obj);
                run.Abort($"{obj.Name} 을(를) 끄지 못해 복사를 시작하지 않았습니다: {Describe(r)}");
                return;
            }
        }
    }

    // ---- 2. 대상 정리 ----

    /// <summary>자식 → 부모 순서로 DELETE. 지우지 못한 표는 적재하지 않는다(겹친 행이 생기므로).</summary>
    private async Task DeleteTargetsAsync(RunState run, CancellationToken ct)
    {
        for (var i = run.Report.Order.Count - 1; i >= 0; i--)
        {
            var plan = run.Report.Order[i];
            var delete = run.Builder.TargetDelete(plan.Rule.Table);
            if (delete is null)
            {
                continue;
            }

            var table = run.Table(plan);
            run.Progress(table.Name, 0, null, PhaseDelete);
            var r = await ExecuteLoggedAsync(run, run.Target, CopyStatements.Delete(delete), delete.Binds, ct);
            if (r.Status != ExecutionStatus.Succeeded)
            {
                table.Fail("대상 정리(DELETE)를 하지 못했습니다: " + Describe(r));
            }
            else
            {
                table.Deleted = true;
            }
        }
    }

    // ---- 3. 적재 ----

    private async Task LoadAllAsync(RunState run, CopyRunControl control, CancellationToken ct)
    {
        foreach (var plan in run.Report.Order)
        {
            if (run.Aborted)
            {
                return;
            }

            var table = run.Table(plan);
            if (table.Outcome == CopyOutcome.Failed)
            {
                continue;
            }

            await LoadTableAsync(run, plan, table, control, ct);
        }
    }

    private async Task LoadTableAsync(RunState run, CopyTablePlan plan, TableState table, CopyRunControl control, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var shape = RowShape.Create(run.Job, plan);
            var dml = CopyStatements.ArrayDml(DmlFor(run.Builder, plan), plan.Columns.Count);
            var estimated = run.Report.EstimatedRows.TryGetValue(plan.Rule.Table, out var e) ? e : (long?)null;
            run.Progress(table.Name, 0, estimated, PhaseLoad);

            // 실패 행 상한을 넘거나 원본 읽기가 실패하면 이 표의 적재분만 되돌린다(앞 표의 정리·적재는 남긴다).
            var savepoint = await ControlAsync(run.Target, CopyStatements.Savepoint(CopyStatements.TableSavepoint), ct);
            if (!RunState.Accept(savepoint, table, "적재를 시작하지 못했습니다", ct))
            {
                return;
            }

            table.SavepointSet = true;
            if (!run.Job.ConsistentRead && !await BeginTableReadAsync(run, table, ct))
            {
                return;
            }

            var select = run.Builder.SourceSelect(plan.Rule.Table, plan.Columns);
            var batch = new List<object?[]>(run.Job.Options.BatchSize);
            var stop = false;
            try
            {
                await foreach (var row in _bulk.StreamRowsAsync(run.Source, CopyStatements.Query(select), select.Binds, run.BulkOptions, ct))
                {
                    batch.Add(shape.Apply(row));
                    table.SourceRows++;
                    if (batch.Count < run.Job.Options.BatchSize)
                    {
                        continue;
                    }

                    stop = !await FlushAsync(run, plan, table, shape, dml, batch, control, ct);
                    batch.Clear();
                    run.Progress(table.Name, table.SourceRows, estimated, PhaseLoad);
                    if (stop)
                    {
                        break;
                    }
                }
            }
            catch (OracleException ex) when (!ct.IsCancellationRequested)
            {
                if (ex.Number == SnapshotTooOld)
                {
                    run.Notices.Add($"{table.Name}: 원본 읽기가 오래 걸려 이전 시점 데이터를 읽지 못했습니다(ORA-01555). 기간을 줄이거나 나눠 실행하세요.");
                    run.Abort(null);
                }

                await RollbackTableAsync(run, table, ct);
                table.Fail("원본을 읽지 못했습니다: " + SafeError(ex.Message));
                return;
            }

            if (!stop && batch.Count > 0)
            {
                stop = !await FlushAsync(run, plan, table, shape, dml, batch, control, ct);
            }

            if (table.Outcome == CopyOutcome.Failed)
            {
                await RollbackTableAsync(run, table, ct);
                return;
            }

            if (run.Job.Options.Commit == CommitMode.PerTable)
            {
                var commit = await ControlAsync(run.Target, CopyStatements.Commit(), ct);
                if (RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
                {
                    run.DeletesPersisted = true;
                    await ConfirmFailedRowsAsync(run, table);
                }
            }

            table.LoadCompleted = table.Outcome != CopyOutcome.Failed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // 규칙·열 정보가 맞지 않음(마스킹 고정값 변환 실패, 키로 쓸 수 없는 형식 등). 이 표만 실패로 두고 다음 표로 간다.
            await RollbackTableAsync(run, table, ct);
            table.Fail(SafeError(ex.Message));
        }
        catch (BulkDeniedException ex)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("정책이 원본 읽기를 거부했습니다: " + SafeError(ex.Message));
        }
        finally
        {
            table.Elapsed += sw.Elapsed;
        }
    }

    /// <summary>
    /// 배치 하나를 보낸다. 실패하면 배치 세이브포인트로 되돌린 뒤 한 행씩 다시 넣어 실패 행을 찾는다(설계 8.2).
    /// 표를 더 진행하면 true, 그만두면(실패 행 상한 초과·거부) false.
    /// </summary>
    private async Task<bool> FlushAsync(RunState run, CopyTablePlan plan, TableState table, RowShape shape, SqlStatement dml,
                                        List<object?[]> batch, CopyRunControl control, CancellationToken ct)
    {
        await control.WaitIfPausedAsync(ct);
        var perBatch = run.Job.Options.Commit == CommitMode.PerBatch;
        var pendingAtBatchStart = table.PendingFailed.Count;
        var savepoint = await ControlAsync(run.Target, CopyStatements.Savepoint(CopyStatements.BatchSavepoint), ct);
        if (!RunState.Accept(savepoint, table, "배치를 시작하지 못했습니다", ct))
        {
            return false;
        }

        var r = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, batch, run.BulkOptions, ct);
        if (r.Status == ExecutionStatus.Succeeded)
        {
            table.Loaded += batch.Count;
        }
        else if (r.Status == ExecutionStatus.Failed)
        {
            // 배열 DML 이 실패하면 실패한 행 앞의 행이 트랜잭션에 남아 있을 수 있다.
            var back = await ControlAsync(run.Target, CopyStatements.RollbackTo(CopyStatements.BatchSavepoint), ct);
            if (!RunState.Accept(back, table, "실패한 배치를 되돌리지 못했습니다", ct))
            {
                return false;
            }

            foreach (var row in batch)
            {
                var one = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, [row], run.BulkOptions, ct);
                if (one.Status == ExecutionStatus.Succeeded)
                {
                    table.Loaded++;
                    continue;
                }

                if (one.Status != ExecutionStatus.Failed)
                {
                    RunState.Accept(one, table, "행을 넣지 못했습니다", ct);
                    return false;
                }

                // 실패 행은 그 표의 적재가 확정(커밋)될 때 결과·실행 기록에 넣는다. 되돌린 적재의 실패 행으로
                // 「실패 행만 다시 넣기」를 하면 나머지 행이 빠진 표가 성공으로 보이기 때문이다.
                table.PendingFailed.Add(new FailedRow(table.Name, shape.KeyOf(row), ErrorText(one.Error)));
                table.FailedRows++;
                if (table.FailedRows > run.Job.Options.MaxFailedRows)
                {
                    // 이 배치에서 한 행씩 넣은 행도 되돌린다(「배치마다 커밋」이면 표 세이브포인트가 없으므로 여기서만 되돌릴 수 있다).
                    var undo = await ControlAsync(run.Target, CopyStatements.RollbackTo(CopyStatements.BatchSavepoint), ct);
                    if (undo.Status == ExecutionStatus.Succeeded)
                    {
                        // 이 배치의 실패 행만 뺀다. 앞 배치의 대기 행은 표 되돌리기가 성공할 때 함께 버린다(RolledBack).
                        table.PendingFailed.RemoveRange(pendingAtBatchStart, table.PendingFailed.Count - pendingAtBatchStart);
                    }
                    else
                    {
                        run.Notices.Add($"{table.Name}: 상한을 넘은 배치를 되돌리지 못했습니다: " + Describe(undo));
                    }

                    table.Fail($"실패 행이 상한({run.Job.Options.MaxFailedRows.ToString(CultureInfo.InvariantCulture)})을 넘어 이 표를 멈췄습니다.");
                    return false;
                }
            }
        }
        else
        {
            RunState.Accept(r, table, "배치를 넣지 못했습니다", ct);
            return false;
        }

        if (perBatch)
        {
            var commit = await ControlAsync(run.Target, CopyStatements.Commit(), ct);
            if (!RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
            {
                return false;
            }

            run.DeletesPersisted = true;
            await ConfirmFailedRowsAsync(run, table);
        }

        return true;
    }

    /// <summary>
    /// 표 시작 세이브포인트로 되돌린다. 세이브포인트를 세우기 전이면(열 배치·문장을 만들다 실패) 되돌릴 것이 없다.
    /// 「배치마다 커밋」은 커밋이 세이브포인트를 지우므로 되돌릴 것이 없다.
    /// </summary>
    private async Task RollbackTableAsync(RunState run, TableState table, CancellationToken ct)
    {
        // 「배치마다 커밋」 적재는 커밋이 세이브포인트를 지운다. 다시 넣기는 커밋 단위와 상관없이 표마다 커밋하므로 되돌릴 수 있다.
        if (!table.SavepointSet || (run.Job.Options.Commit == CommitMode.PerBatch && !run.IsRetry))
        {
            return;
        }

        var token = ct.IsCancellationRequested ? CancellationToken.None : ct;
        var r = await ControlAsync(run.Target, CopyStatements.RollbackTo(CopyStatements.TableSavepoint), token);
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Notices.Add("실패한 표의 적재분을 되돌리지 못했습니다: " + Describe(r));
            return;
        }

        table.RolledBack();
    }

    /// <summary>
    /// 확정(커밋)된 표의 실패 행을 결과와 실행 기록에 넣는다. 「실패 행만 다시 넣기」는 이 목록만 쓴다.
    /// 기록 실패는 예외로 올리지 않는다(행은 이미 커밋됐고, 결과에는 남는다).
    /// </summary>
    private static async Task ConfirmFailedRowsAsync(RunState run, TableState table)
    {
        // 커밋은 이미 끝났다: 결과에 먼저 옮기고 확정 표시를 한 뒤 기록한다. 기록은 중지 토큰과 상관없는 새 토큰으로,
        // 실패(시간 초과 포함)는 여기서 가둔다(호출한 쪽의 마무리·다시 켜기를 끊지 않게).
        var rows = table.PendingFailed.ToList();
        table.PendingFailed.Clear();
        table.Confirmed = true;
        run.FailedRows.AddRange(rows);
        if (run.Record is not { } record || rows.Count == 0)
        {
            return;
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            foreach (var row in rows)
            {
                await record.RecordFailedRowAsync(row, step.Token);
            }
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Notices.Add("실행 기록에 실패 행을 남기지 못했습니다: " + SafeError(ex.Message));
        }
    }

    /// <summary>
    /// 「같은 시점 기준 읽기」를 끈 경우: 표를 읽기 전마다 원본 트랜잭션을 닫고 읽기 전용 트랜잭션을 새로 연다
    /// (표마다 따로 읽되, 원본에는 언제나 DB 수준 READ ONLY 가 걸려 있게 한다).
    /// </summary>
    private async Task<bool> BeginTableReadAsync(RunState run, TableState table, CancellationToken ct)
    {
        var rollback = await ControlAsync(run.Source, CopyStatements.Rollback(), ct);
        if (!RunState.Accept(rollback, table, "원본 트랜잭션을 정리하지 못했습니다", ct))
        {
            return false;
        }

        var set = await ControlAsync(run.Source, CopyStatements.SetTransactionReadOnly(), ct);
        return RunState.Accept(set, table, "원본에서 읽기 전용 트랜잭션을 시작하지 못했습니다", ct);
    }

    private static string DmlFor(CopySqlBuilder builder, CopyTablePlan plan)
    {
        var table = plan.Rule.Table;
        // 사전 점검의 필요 권한(UPDATE)과 같은 판정을 쓴다.
        var merge = PrecheckRules.UsesMerge(plan.Rule, plan.KeyColumns);
        if (!merge && plan.Rule.Action == TargetAction.MergeByPk)
        {
            throw new InvalidOperationException($"{table}: PK 기준 MERGE 에 쓸 키 열이 없습니다.");
        }

        return merge ? builder.Merge(table, plan.Columns, plan.KeyColumns) : builder.Insert(table, plan.Columns);
    }

    // ---- 「실패 행만 다시 넣기」 ----

    private async Task RetryTableAsync(RunState run, string tableName, IReadOnlyList<FailedRow> rows, CancellationToken ct)
    {
        var plan = run.Report.Order.FirstOrDefault(p => string.Equals(p.Rule.Table, tableName, StringComparison.Ordinal));
        if (plan is null)
        {
            run.Notices.Add($"{tableName}: 사전 점검 결과에 없는 표라 다시 넣지 않았습니다.");
            return;
        }

        var table = run.Table(plan);
        var sw = Stopwatch.StartNew();
        try
        {
            if (plan.KeyColumns.Count == 0)
            {
                table.Fail("키 열이 없어 실패 행을 다시 읽을 수 없습니다.");
                return;
            }

            var shape = RowShape.Create(run.Job, plan);
            var dml = CopyStatements.ArrayDml(DmlFor(run.Builder, plan), plan.Columns.Count);
            var query = CopyStatements.Query(run.Builder.SourceSelectByKeys(tableName, plan.Columns, plan.KeyColumns));
            var savepoint = await ControlAsync(run.Target, CopyStatements.Savepoint(CopyStatements.TableSavepoint), ct);
            if (!RunState.Accept(savepoint, table, "다시 넣기를 시작하지 못했습니다", ct))
            {
                return;
            }

            table.SavepointSet = true;
            foreach (var failedRow in rows)
            {
                run.Progress(tableName, table.SourceRows, rows.Count, PhaseRetry);
                var binds = shape.KeyBinds(failedRow);
                object?[]? source = null;
                await foreach (var row in _bulk.StreamRowsAsync(run.Source, query, binds, run.BulkOptions, ct))
                {
                    source = shape.Apply(row);
                }

                table.SourceRows++;
                if (source is null)
                {
                    // 목록에서 빼지 않는다: 사용자가 그 행이 왜 안 들어갔는지 결과에서 봐야 한다.
                    table.PendingFailed.Add(failedRow with { Error = "원본에 이 키의 행이 없습니다(그 사이 지워졌거나 키 값이 바뀌었습니다)." });
                    table.FailedRows++;
                    continue;
                }

                var one = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, [source], run.BulkOptions, ct);
                if (one.Status == ExecutionStatus.Succeeded)
                {
                    table.Loaded++;
                    continue;
                }

                if (one.Status != ExecutionStatus.Failed)
                {
                    RunState.Accept(one, table, "행을 넣지 못했습니다", ct);
                    await RollbackTableAsync(run, table, ct);
                    return;
                }

                table.PendingFailed.Add(failedRow with { Error = ErrorText(one.Error) });
                table.FailedRows++;
            }

            // 다시 넣기는 표마다 커밋한다. 커밋하지 못한 표의 입력 행은 끝낼 때 그대로 돌려준다(FinishAsync).
            var commit = await ControlAsync(run.Target, CopyStatements.Commit(), ct);
            if (RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
            {
                await ConfirmFailedRowsAsync(run, table);
                table.LoadCompleted = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FormatException)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail(SafeError(ex.Message));
        }
        catch (BulkDeniedException ex)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("정책이 원본 읽기를 거부했습니다: " + SafeError(ex.Message));
        }
        catch (OracleException ex) when (!ct.IsCancellationRequested)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("원본을 다시 읽지 못했습니다: " + SafeError(ex.Message));
        }
        finally
        {
            table.Elapsed += sw.Elapsed;
        }
    }

    // ---- 4. 시퀀스 보정 ----

    private async Task AdvanceSequencesAsync(RunState run, bool proceedLargeGap, CancellationToken ct)
    {
        // 읽기 전용 대상은 NEXTVAL 조회도 시퀀스 상태를 바꾸므로 보정하지 않는다(조회로 분류돼 정책을 지난다).
        if (run.Target.Profile.ReadOnly)
        {
            return;
        }

        foreach (var rule in run.Job.Sequences)
        {
            // 적재를 끝내고 되돌리지 않은 표만 보정한다(실패·되돌린 표의 시퀀스는 그대로 둔다).
            if (!run.Tables.TryGetValue(rule.Table, out var loaded)
                || !loaded.LoadCompleted || loaded.WasRolledBack || loaded.Outcome == CopyOutcome.Failed || loaded.Loaded == 0)
            {
                continue;
            }

            run.Progress(rule.Table, 0, null, PhaseSequence);
            var owner = run.Job.TargetOwner;
            try
            {
                var next = CopyStatements.NextValues(owner, rule.Sequence);
                var maxCell = await ScalarAsync(run, run.Target,
                    CopyStatements.Query($"SELECT MAX({SqlIdentifier.QuoteAlways(rule.Column)}) FROM {CopyStatements.Qualified(owner, rule.Table)}"),
                    NoBinds, ct);
                if (maxCell is not OracleDecimal { IsNull: false } maxValue)
                {
                    continue; // 대상 표가 비었다.
                }

                var info = await RowsAsync(run, run.Target, CopyStatements.Query(SequenceIncrementSql),
                    new Dictionary<string, BindValue>
                    {
                        ["OWNER"] = new(BindDataType.Varchar2, owner),
                        ["NAME"] = new(BindDataType.Varchar2, rule.Sequence),
                    }, ct);
                if (info.Count == 0 || info[0][0] is not OracleDecimal { IsNull: false } inc)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: SELECT 권한이 없어 보정하지 않았습니다. 테스트 중 PK 충돌이 날 수 있습니다.");
                    continue;
                }

                long need;
                try
                {
                    var step = ToLong(inc);
                    var max = ToLong(maxValue);

                    // 다음 값은 LAST_NUMBER − 캐시 × 증가값 보다 작을 수 없다. 그것이 이미 최대값보다 크면 NEXTVAL 을 하나도 쓰지 않는다.
                    // RAC NOORDER 는 다른 인스턴스 캐시에 더 작은 값이 남을 수 있어 이 하한을 쓰지 않는다.
                    if (!run.Target.Server.IsRac && info[0][1] is OracleDecimal { IsNull: false } last && info[0][2] is OracleDecimal { IsNull: false } cache
                        && step > 0 && checked(ToLong(last) - (ToLong(cache) * step)) > max)
                    {
                        continue;
                    }

                    var first = await ScalarAsync(run, run.Target, next, CountBind(1), ct);
                    if (first is not OracleDecimal { IsNull: false } current)
                    {
                        continue;
                    }

                    need = SequenceAdvance.Plan(checked(ToLong(current) + step), max, step);
                }
                catch (OverflowException)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: 값이 너무 커서 보정하지 않았습니다.");
                    continue;
                }

                if (need >= run.Job.Options.SequenceGapConfirm && !proceedLargeGap)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: 모자란 수({need.ToString(CultureInfo.InvariantCulture)})가 커서 보정을 건너뛰었습니다.");
                    continue;
                }

                for (var left = need; left > 0;)
                {
                    var chunk = Math.Min(left, SequenceAdvance.ChunkSize);
                    await foreach (var _ in _bulk.StreamRowsAsync(run.Target, next, CountBind(chunk), run.BulkOptions, ct))
                    {
                        // 행을 끝까지 읽어야 그만큼 소비된다.
                    }

                    left -= chunk;
                }

                if (need > 0)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: NEXTVAL {need.ToString(CultureInfo.InvariantCulture)}개를 소비해 {rule.Table}.{rule.Column} 최대값 뒤로 옮겼습니다.");
                }
            }
            catch (OracleException ex) when (!ct.IsCancellationRequested)
            {
                run.Notices.Add($"시퀀스 {rule.Sequence}: 보정하지 못했습니다({SafeError(ex.Message)}). 테스트 중 PK 충돌이 날 수 있습니다.");
            }
            catch (BulkDeniedException ex)
            {
                run.Notices.Add($"시퀀스 {rule.Sequence}: 정책이 거부해 보정하지 않았습니다({SafeError(ex.Message)}).");
            }
        }
    }

    /// <summary>NUMBER → long. 범위를 넘으면 <see cref="OverflowException"/>(호출한 쪽이 알림으로 바꾼다).</summary>
    private static long ToLong(OracleDecimal value)
    {
        try
        {
            return (long)value;
        }
        catch (InvalidCastException ex)
        {
            throw new OverflowException("NUMBER 값이 long 범위를 넘습니다.", ex);
        }
    }

    private static Dictionary<string, BindValue> CountBind(long n) =>
        new() { [CopyStatements.NextValuesBind] = new BindValue(BindDataType.Number, (decimal)n) };

    // ---- 5. 끝내기(트랜잭션 → 다시 켜기 → 건수 → 기록) ----

    /// <summary>
    /// 중지·예외 뒤에도 반드시 지나간다. <c>ct</c> 를 쓰지 않고 단계마다 새 토큰을 쓴다(한 단계가 시간을 다 써도 다음 단계는 돈다).
    /// 대상 트랜잭션을 끝내지 못했으면 다시 켜기(DDL, 묵시 커밋)를 하지 않는다: 켜면 되돌리려던 행이 확정된다.
    /// </summary>
    private async Task FinishAsync(RunState run, bool faulted)
    {
        var ended = false;
        try
        {
            ended = await EndTargetTransactionAsync(run, faulted);
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Notices.Add("대상 트랜잭션을 끝내지 못했습니다: " + SafeError(ex.Message));
        }

        if (ended)
        {
            run.EnableFailed = !await EnableAsync(run);
        }
        else if (run.Disabled.Count > 0)
        {
            run.EnableFailed = true;
            run.Notices.Add("대상 트랜잭션을 끝내지 못해 끈 트리거·FK 를 다시 켜지 않았습니다(켜면 미커밋 행이 확정됩니다). 세션을 정리한 뒤 직접 켜세요: "
                            + string.Join(", ", run.Disabled.Select(d => d.Owner + "." + d.Name)));
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            var rollback = await ControlAsync(run.Source, CopyStatements.Rollback(), step.Token);
            if (rollback.Status != ExecutionStatus.Succeeded)
            {
                run.Notices.Add("원본 읽기 전용 트랜잭션을 닫지 못했습니다: " + Describe(rollback));
            }
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Notices.Add("원본 읽기 전용 트랜잭션을 닫지 못했습니다: " + SafeError(ex.Message));
        }

        if (!run.IsRetry)
        {
            // 적재를 끝내지 못한 표(상한 초과·중지·ORA-01555)는 「배치마다 커밋」이면 앞 배치의 실패 행이 확정돼 있다.
            // 그 행만 다시 넣으면 나머지 배치가 빠진 표가 성공으로 보이므로 결과에서 빼고 다시 실행하라고 적는다.
            foreach (var t in run.Tables.Values.Where(t => !t.LoadCompleted))
            {
                if (run.FailedRows.RemoveAll(r => r.Table == t.Name) > 0)
                {
                    t.Note("적재를 끝내지 못해 「실패 행만 다시 넣기」를 쓸 수 없습니다. 다시 실행하세요.");
                }
            }
        }

        if (run.IsRetry)
        {
            // 확정(커밋)하지 못한 표의 입력 행은 사라지지 않게 그대로 돌려준다(중지·실패·시작하지 못한 표).
            var confirmed = run.Tables.Values.Where(t => t.Confirmed).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var unconfirmed = run.RetryInputs.Where(r => !confirmed.Contains(r.Table)).ToList();
            run.FailedRows.AddRange(unconfirmed);
            if (run.Record is { } retryRecord)
            {
                try
                {
                    using var step = new CancellationTokenSource(CleanupTimeout);
                    foreach (var row in unconfirmed)
                    {
                        await retryRecord.RecordFailedRowAsync(row, step.Token);
                    }
                }
                catch (Exception ex) when (IsCleanupFailure(ex))
                {
                    run.Notices.Add("실행 기록에 실패 행을 남기지 못했습니다: " + SafeError(ex.Message));
                }
            }

            // 다시 넣기는 기간 조건 없이 키로 읽으므로 「같은 조건의 대상 건수」가 없다. 넣은 행 수를 보인다.
            foreach (var t in run.Tables.Values)
            {
                t.TargetRows = t.Loaded;
            }
        }
        else if (!run.Stopped && !faulted)
        {
            using var step = new CancellationTokenSource(LongCleanupTimeout);
            await CountTargetsAsync(run, step.Token);
        }

        NoteDeletedButNotLoaded(run);

        if (run.Record is not { } record)
        {
            return;
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            foreach (var t in run.Tables.Values)
            {
                await record.RecordTableAsync(t.ToResult(), step.Token);
            }

            // 다시 켜지 못한 객체가 있으면 기록을 끝내지 않는다: 다음에 작업을 열 때 「다시 켜기」 목록으로 보인다(설계 9장).
            if (!run.EnableFailed)
            {
                await record.CompleteAsync(run.Outcome(faulted), step.Token);
            }
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Notices.Add("실행 기록을 마무리하지 못했습니다: " + SafeError(ex.Message));
        }
    }

    private static bool IsCleanupFailure(Exception ex) =>
        ex is OracleException or OperationCanceledException or InvalidOperationException or IOException or BulkDeniedException;

    /// <summary>
    /// 정리(DELETE)는 모든 표에 먼저 하므로(설계 8.2, 자식 → 부모), 한 표의 커밋이 다른 표의 DELETE 도 확정한다.
    /// 적재를 끝내지 못한 표의 DELETE 가 확정됐으면 그 사실을 표 결과와 알림에 적는다(다시 실행하면 같은 결과가 된다).
    /// </summary>
    private static void NoteDeletedButNotLoaded(RunState run)
    {
        if (run.Stopped)
        {
            foreach (var t in run.Tables.Values.Where(t => !t.LoadCompleted))
            {
                t.MarkStopped();
            }
        }

        if (!run.DeletesPersisted)
        {
            return;
        }

        var tables = run.Tables.Values.Where(t => t.Deleted && !t.LoadCompleted).ToList();
        foreach (var t in tables)
        {
            t.Note("대상 정리(DELETE)는 확정됐지만 적재를 끝내지 못했습니다. 다시 실행하세요.");
        }

        if (tables.Count > 0)
        {
            run.Notices.Add("대상 행을 지운 뒤 적재를 끝내지 못한 표가 있습니다(" + string.Join(", ", tables.Select(t => t.Name))
                            + "). 다시 실행하면 같은 결과가 됩니다.");
        }
    }

    /// <summary>다시 켜기(DDL, 묵시 커밋) 전에 대상 트랜잭션을 끝낸다. 끝났으면(커밋 또는 롤백 성공) true.</summary>
    private async Task<bool> EndTargetTransactionAsync(RunState run, bool faulted)
    {
        // 전체 중단(원본 준비·끄기 실패, ORA-01555)도 되돌린다: 커밋하면 아직 적재하지 않은 뒤쪽 표의 DELETE 까지 확정된다.
        var rollback = run.Stopped || faulted || run.Aborted
                       || (run.Job.Options.Commit == CommitMode.AtEnd && run.Tables.Values.Any(t => t.Outcome == CopyOutcome.Failed));
        if (!rollback)
        {
            using var commitStep = new CancellationTokenSource(CleanupTimeout);
            var commit = await ControlAsync(run.Target, CopyStatements.Commit(), commitStep.Token);
            if (commit.Status == ExecutionStatus.Succeeded)
            {
                run.DeletesPersisted = true;
                foreach (var t in run.Tables.Values.Where(t => !t.WasRolledBack && (t.PendingFailed.Count > 0 || t.LoadCompleted)))
                {
                    await ConfirmFailedRowsAsync(run, t);
                }

                return true;
            }

            run.CommitFailed = true;
            run.Notices.Add("대상 커밋을 하지 못해 되돌립니다: " + Describe(commit));
        }

        // 큰 적재를 되돌리는 ROLLBACK 은 오래 걸릴 수 있다.
        using var step = new CancellationTokenSource(LongCleanupTimeout);
        var r = await ControlAsync(run.Target, CopyStatements.Rollback(), step.Token);
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Notices.Add("대상 롤백을 하지 못했습니다: " + Describe(r));
            return false;
        }

        // 확정되지 않은 적재는 모두 되돌아갔다: 그 실패 행은 버린다.
        foreach (var t in run.Tables.Values.Where(t => t.PendingFailed.Count > 0 || (!t.Confirmed && t.FailedRows > 0)))
        {
            t.RolledBack();
        }

        if (run.Job.Options.Commit == CommitMode.AtEnd && !run.Stopped)
        {
            run.Notices.Add("「모두 끝난 뒤 한 번 커밋」이라 실패 때문에 모든 변경을 되돌렸습니다.");
            run.RolledBackAll = true;
        }

        return true;
    }

    /// <summary>
    /// 끈 순서의 반대로 켠다. FK 는 VALIDATE 가 실패하면 NOVALIDATE 로 켜고 부모 없는 행 수를 알린다. 모두 켰으면 true.
    /// 객체마다 따로 토큰을 주고 예외를 가둔다: 하나가 실패해도(시간 초과·기록 실패 포함) 나머지는 켠다.
    /// </summary>
    private async Task<bool> EnableAsync(RunState run)
    {
        var allEnabled = true;
        for (var i = run.Disabled.Count - 1; i >= 0; i--)
        {
            var obj = run.Disabled[i];
            run.Progress(obj.Table, 0, null, PhaseEnable);
            try
            {
                if (obj.Kind == DisabledKind.Trigger)
                {
                    var r = await CleanupStatementAsync(run, CopyStatements.EnableTrigger(obj.Owner, obj.Name), CleanupTimeout);
                    if (r.Status != ExecutionStatus.Succeeded)
                    {
                        allEnabled = false;
                        run.Notices.Add($"트리거 {obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {Describe(r)}");
                    }

                    continue;
                }

                // VALIDATE 는 자식 표 전체를 읽으므로 오래 걸릴 수 있다.
                var validated = await CleanupStatementAsync(run, CopyStatements.EnableConstraint(obj, validate: true), LongCleanupTimeout);
                if (validated.Status == ExecutionStatus.Succeeded)
                {
                    continue;
                }

                var loose = await CleanupStatementAsync(run, CopyStatements.EnableConstraint(obj, validate: false), CleanupTimeout);
                if (loose.Status != ExecutionStatus.Succeeded)
                {
                    allEnabled = false;
                    run.Notices.Add($"FK {obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {Describe(loose)}");
                    continue;
                }

                using var count = new CancellationTokenSource(LongCleanupTimeout);
                var orphans = await OrphanCountAsync(run, obj, count.Token);
                run.Notices.Add($"FK {obj.Owner}.{obj.Name}: 부모 없는 행 {orphans} 때문에 ENABLE NOVALIDATE 로 켰습니다.");
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                allEnabled = false;
                run.Notices.Add($"{obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {SafeError(ex.Message)}");
            }
        }

        return allEnabled;
    }

    /// <summary>정리 문장 하나를 자기 토큰으로 실행하고, 성공하면 실행 기록에 남긴다. 기록 실패는 문장 결과를 바꾸지 않는다.</summary>
    private async Task<ExecutionResult> CleanupStatementAsync(RunState run, SqlStatement stmt, TimeSpan timeout)
    {
        using var step = new CancellationTokenSource(timeout);
        var r = await _execution.ExecuteAsync(run.Target, stmt, NoBinds, run.HistoryOptions, step.Token);
        if (r.Status == ExecutionStatus.Succeeded && run.Record is { } record)
        {
            try
            {
                await record.RecordStatementAsync(stmt.Text, step.Token);
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                run.Notices.Add("실행 기록에 문장을 남기지 못했습니다: " + SafeError(ex.Message));
            }
        }

        return r;
    }

    private async Task<string> OrphanCountAsync(RunState run, DisabledObject fk, CancellationToken token)
    {
        try
        {
            var cols = await RowsAsync(run, run.Target, CopyStatements.Query(ForeignKeyColumnsSql),
                new Dictionary<string, BindValue>
                {
                    ["OWNER"] = new(BindDataType.Varchar2, fk.Owner),
                    ["NAME"] = new(BindDataType.Varchar2, fk.Name),
                }, token);
            if (cols.Count == 0)
            {
                return "수 모름";
            }

            var child = cols.Select(c => SqlIdentifier.QuoteAlways((string)c[0]!)).ToList();
            var parent = cols.Select(c => SqlIdentifier.QuoteAlways((string)c[1]!)).ToList();
            var parentTable = CopyStatements.Qualified((string)cols[0][2]!, (string)cols[0][3]!);
            var notNull = string.Join(" AND ", child.Select(c => $"c.{c} IS NOT NULL"));
            var join = string.Join(" AND ", child.Select((c, i) => $"p.{parent[i]} = c.{c}"));
            var sql = $"SELECT COUNT(*) FROM {CopyStatements.Qualified(fk.Owner, fk.Table)} c WHERE {notNull}"
                      + $" AND NOT EXISTS (SELECT 1 FROM {parentTable} p WHERE {join})";
            var n = await ScalarAsync(run, run.Target, CopyStatements.Query(sql), NoBinds, token);
            return n is OracleDecimal { IsNull: false } d ? d.ToString() + "건" : "수 모름";
        }
        catch (Exception ex) when (ex is OracleException or BulkDeniedException)
        {
            return "수 모름";
        }
    }

    /// <summary>원본 건수 = 적재할 때 읽은 행 수, 대상 건수 = 같은 조건으로 대상에서 COUNT(*).</summary>
    private async Task CountTargetsAsync(RunState run, CancellationToken token)
    {
        foreach (var table in run.Tables.Values)
        {
            run.Progress(table.Name, table.SourceRows, null, PhaseCount);
            try
            {
                var count = run.Builder.TargetCount(table.Name);
                var n = await ScalarAsync(run, run.Target, CopyStatements.Query(count), count.Binds, token);
                table.TargetRows = n is OracleDecimal { IsNull: false } d ? ToLong(d) : -1;
            }
            catch (Exception ex) when (ex is OracleException or BulkDeniedException or InvalidOperationException)
            {
                table.TargetRows = -1;
                table.Note("대상 건수를 세지 못했습니다: " + SafeError(ex.Message));
            }
        }
    }

    // ---- 실행 도우미 ----

    /// <summary>바인드 없는 트랜잭션 제어(대량 경로: 읽기 전용 원본에서도 SET TRANSACTION·COMMIT·ROLLBACK 은 지난다).</summary>
    private Task<BulkResult> ControlAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
        _bulk.ExecuteArrayAsync(s, stmt, [], [], new ExecutionOptions { Source = HistorySource.DataCopy, SkipHistory = true }, ct);

    /// <summary>DELETE·ALTER: 실행 엔진(정책·이력)으로 보내고, 성공하면 실행 기록에도 같은 글을 남긴다.</summary>
    private async Task<ExecutionResult> ExecuteLoggedAsync(RunState run, ISession s, SqlStatement stmt,
                                                           IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var r = await _execution.ExecuteAsync(s, stmt, binds, run.HistoryOptions, ct);
        if (r.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (r.Status == ExecutionStatus.Succeeded && run.Record is { } record)
        {
            await record.RecordStatementAsync(stmt.Text, ct);
        }

        return r;
    }

    private async Task<List<object?[]>> RowsAsync(RunState run, ISession s, SqlStatement query,
                                                 IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await foreach (var row in _bulk.StreamRowsAsync(s, query, binds, run.BulkOptions, ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private async Task<object?> ScalarAsync(RunState run, ISession s, SqlStatement query,
                                            IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var rows = await RowsAsync(run, s, query, binds, ct);
        return rows.Count > 0 && rows[0].Length > 0 ? rows[0][0] : null;
    }

    private static string Describe(ExecutionResult r) =>
        r.Status switch
        {
            ExecutionStatus.Denied => "정책 거부: " + (r.Policy?.Message ?? "거부"),
            ExecutionStatus.Cancelled => "확인을 받지 못해 실행하지 않았습니다" + (r.Policy?.Message is { } m ? ": " + m : string.Empty),
            _ => ErrorText(r.Error),
        };

    internal static string Describe(BulkResult r) =>
        r.Status switch
        {
            ExecutionStatus.Denied => "정책 거부: " + (r.Policy?.Message ?? "거부"),
            ExecutionStatus.Cancelled => "확인을 받지 못해 실행하지 않았습니다" + (r.Policy?.Message is { } m ? ": " + m : string.Empty),
            _ => ErrorText(r.Error),
        };

    /// <summary>실패 행 기록에는 오류 문구만 남긴다(서버 오류 문구에는 행 값이 들어가지 않는다).</summary>
    private static string ErrorText(DbError? e) =>
        e is null ? "알 수 없는 오류"
        : SafeError(e.Message.StartsWith(e.Code, StringComparison.Ordinal) ? e.Message : e.Code + ": " + e.Message);

    /// <summary>
    /// 서버 오류 문구에서 행 값을 지운다. 23ai 부터 일부 오류가 값을 문구에 넣는다
    /// (ORA-01722 「unable to convert string value containing '…'」, ORA-03301 「row with column values (…)」).
    /// 첫 줄만 남기고 작은따옴표 안의 글자와 「column values (…)」 를 지운다. 열·제약 이름(큰따옴표)은 남긴다.
    /// 실행 기록·표 결과·알림에 남는 오류 문구는 모두 이것을 거친다(행 값은 디스크에 남기지 않는다, 설계 4장).
    /// </summary>
    internal static string SafeError(string message)
    {
        var line = message.Split('\n', 2)[0].TrimEnd('\r');
        line = QuotedValue.Replace(line, "'…'");
        return ColumnValues.Replace(line, "column values (…)");
    }


    // ---- 상태 ----

    /// <summary>표 하나의 열 배치: 바인드 열(원본 형식), 마스킹, 키 열 위치.</summary>
    private sealed class RowShape
    {
        private readonly (int Index, MaskRule Rule, int MaxLength, object? Fixed, bool Text)[] _masks;
        private readonly int[] _keyIndexes;
        private readonly string[] _keyTypes;
        private readonly string _salt;

        private RowShape(IReadOnlyList<BulkColumn> columns, (int, MaskRule, int, object?, bool)[] masks, int[] keyIndexes,
                         string[] keyTypes, IReadOnlyList<string> keyNames, string salt)
        {
            Columns = columns;
            _masks = masks;
            _keyIndexes = keyIndexes;
            _keyTypes = keyTypes;
            KeyNames = keyNames;
            _salt = salt;
        }

        public IReadOnlyList<BulkColumn> Columns { get; }

        public IReadOnlyList<string> KeyNames { get; }

        public static RowShape Create(CopyJob job, CopyTablePlan plan)
        {
            var source = plan.Source.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var target = plan.Target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            ColumnInfo SourceColumn(string name) => source.TryGetValue(name, out var c)
                ? c
                : throw new InvalidOperationException($"{plan.Rule.Table}: 원본에 열 {name} 이(가) 없습니다.");

            // 바인드 형식은 원본 열 형식이다: 값이 원본 형식의 ODP.NET 값이고, 형식이 달라도 변환은 서버가 맡는다.
            var bindNames = CopyStatements.BindNames(plan.Columns.Count);
            var columns = plan.Columns.Select((c, i) => new BulkColumn(bindNames[i], SourceColumn(c).DataType)).ToList();

            var masks = new List<(int, MaskRule, int, object?, bool)>();
            foreach (var rule in plan.Rule.Masks)
            {
                var index = IndexOf(plan.Columns, rule.Column);
                if (index < 0)
                {
                    continue; // 복사하지 않는 열이다.
                }

                if (plan.KeyColumns.Contains(rule.Column, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 키 열에는 마스킹을 걸 수 없습니다.");
                }

                var col = SourceColumn(rule.Column);
                var dbType = BulkBinds.ToDbType(col.DataType);
                var text = dbType is OracleDbType.Varchar2 or OracleDbType.NVarchar2 or OracleDbType.Char or OracleDbType.NChar;
                if (!text && rule.Kind is not (MaskKind.Fixed or MaskKind.Null))
                {
                    throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 문자 열이 아니면 고정값·NULL 마스킹만 쓸 수 있습니다.");
                }

                var maxLength = (target.TryGetValue(rule.Column, out var t) ? t.Length : null) ?? col.Length ?? 64;
                object? fixedValue = null;
                if (!text && rule.Kind == MaskKind.Fixed && rule.Value is { } v)
                {
                    try
                    {
                        fixedValue = dbType switch
                        {
                            OracleDbType.Clob or OracleDbType.NClob => v,
                            OracleDbType.Blob => Convert.FromHexString(v),
                            _ => FailedRowKeys.FromText(v, col.DataType),
                        };
                    }
                    catch (Exception ex) when (ex is FormatException or NotSupportedException or OverflowException)
                    {
                        throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 마스킹 고정값을 열 형식으로 바꾸지 못했습니다({ex.Message}).", ex);
                    }
                }

                masks.Add((index, rule, maxLength, fixedValue, text));
            }

            var keyIndexes = plan.KeyColumns.Select(k => IndexOf(plan.Columns, k)).ToArray();
            if (keyIndexes.Any(i => i < 0))
            {
                throw new InvalidOperationException($"{plan.Rule.Table}: 키 열이 복사 열에 없습니다.");
            }

            var keyTypes = plan.KeyColumns.Select(k => SourceColumn(k).DataType).ToArray();
            return new RowShape(columns, [.. masks], keyIndexes, keyTypes, plan.KeyColumns, job.MaskSalt);
        }

        /// <summary>원본 행에 마스킹을 건 새 행. 원본 값은 어디에도 남기지 않는다.</summary>
        public object?[] Apply(object?[] row)
        {
            foreach (var (index, rule, maxLength, fixedValue, text) in _masks)
            {
                row[index] = text
                    ? MaskFunctions.Apply(rule, row[index] as string, _salt, maxLength)
                    : rule.Kind == MaskKind.Fixed ? fixedValue : null;
            }

            return row;
        }

        /// <summary>실패 행 기록용 키 글자. 키 열이 없으면 빈 사전(다시 넣기 불가).</summary>
        public Dictionary<string, string?> KeyOf(object?[] row)
        {
            var key = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < _keyIndexes.Length; i++)
            {
                key[KeyNames[i]] = FailedRowKeys.ToText(row[_keyIndexes[i]], _keyTypes[i]);
            }

            return key;
        }

        /// <summary>실패 행 키 글자 → 다시 읽기 바인드(<c>K1</c>…<c>Kn</c>).</summary>
        public Dictionary<string, BindValue> KeyBinds(FailedRow row)
        {
            var binds = new Dictionary<string, BindValue>(StringComparer.Ordinal);
            for (var i = 0; i < KeyNames.Count; i++)
            {
                if (!row.Key.TryGetValue(KeyNames[i], out var text))
                {
                    throw new InvalidOperationException($"{row.Table}: 실패 행 기록에 키 열 {KeyNames[i]} 이(가) 없습니다.");
                }

                binds["K" + (i + 1).ToString(CultureInfo.InvariantCulture)] =
                    new BindValue(FailedRowKeys.BindTypeOf(_keyTypes[i]), FailedRowKeys.FromText(text, _keyTypes[i]));
            }

            return binds;
        }

        private static int IndexOf(IReadOnlyList<string> columns, string name)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    private sealed class TableState(string name)
    {
        private readonly List<string> _messages = [];

        public string Name { get; } = name;

        public long SourceRows { get; set; }

        public long Loaded { get; set; }

        public long TargetRows { get; set; } = -1;

        public int FailedRows { get; set; }

        /// <summary>확정(커밋)을 기다리는 실패 행.</summary>
        public List<FailedRow> PendingFailed { get; } = [];

        /// <summary>이 표의 적재가 커밋으로 확정됐다(실패 행을 결과에 넣었다).</summary>
        public bool Confirmed { get; set; }

        /// <summary>이 표의 적재를 되돌렸다(실패 행 목록을 비웠다).</summary>
        public bool WasRolledBack { get; private set; }

        /// <summary>적재를 되돌렸다: 실패 행은 「실패 행만 다시 넣기」에 쓸 수 없으므로 버리고, 다시 실행하라고 적는다.</summary>
        public void RolledBack()
        {
            if (!WasRolledBack && (PendingFailed.Count > 0 || FailedRows > 0))
            {
                Note("적재를 되돌려 확정되지 않은 실패 행을 버렸습니다. 「실패 행만 다시 넣기」 대신 다시 실행하세요.");
            }

            WasRolledBack = true;
            PendingFailed.Clear();
        }

        /// <summary>대상 정리(DELETE)를 했다.</summary>
        public bool Deleted { get; set; }

        /// <summary>적재를 끝까지 했다(실패 행이 있어도 표를 멈추지 않았다).</summary>
        public bool LoadCompleted { get; set; }

        /// <summary>표 시작 세이브포인트를 세웠다. 세우기 전에는 되돌리지 않는다(ORA-01086).</summary>
        public bool SavepointSet { get; set; }

        public TimeSpan Elapsed { get; set; }

        public CopyOutcome Outcome { get; private set; } = CopyOutcome.Succeeded;

        public void Fail(string message)
        {
            Outcome = CopyOutcome.Failed;
            _messages.Add(message);
        }

        public void Note(string message) => _messages.Add(message);

        /// <summary>중지로 적재를 끝내지 못한 표(실패로 판정된 표는 그대로 둔다).</summary>
        public void MarkStopped()
        {
            if (Outcome != CopyOutcome.Failed)
            {
                Outcome = CopyOutcome.Stopped;
            }
        }

        public TableRunResult ToResult()
        {
            var outcome = Outcome == CopyOutcome.Succeeded && FailedRows > 0 ? CopyOutcome.PartiallyFailed : Outcome;
            // 표 하나가 「중지」여도 실행 전체 판정(RunState.Outcome)은 따로 한다.
            var message = _messages.Count > 0 ? string.Join(" ", _messages) : null;
            return new TableRunResult(Name, SourceRows, TargetRows, Elapsed, outcome, message);
        }
    }

    private sealed class RunState
    {
        private readonly IProgress<CopyProgress>? _progress;
        private readonly Dictionary<string, TableInfo> _sourceTables;
        private readonly Dictionary<string, TableInfo> _targetTables;

        public RunState(CopySessions sessions, CopyJob job, PrecheckReport report, ResolvedPeriod period,
                        IReadOnlySet<string> preConfirmed, IProgress<CopyProgress>? progress)
        {
            Source = sessions.Source;
            Target = sessions.Target;
            Job = job;
            Report = report;
            _progress = progress;
            _sourceTables = report.Order.ToDictionary(p => p.Rule.Table, p => p.Source, StringComparer.Ordinal);
            _targetTables = report.Order.ToDictionary(p => p.Rule.Table, p => p.Target, StringComparer.Ordinal);
            Builder = new CopySqlBuilder(job, period, _sourceTables, _targetTables);
            HistoryOptions = new ExecutionOptions { Source = HistorySource.DataCopy, PreConfirmed = preConfirmed };
            BulkOptions = new ExecutionOptions { Source = HistorySource.DataCopy, SkipHistory = true, PreConfirmed = preConfirmed };
        }

        public ISession Source { get; }

        public ISession Target { get; }

        public CopyJob Job { get; }

        public PrecheckReport Report { get; }

        public CopySqlBuilder Builder { get; }

        /// <summary>DELETE·ALTER: 이력에 남긴다.</summary>
        public ExecutionOptions HistoryOptions { get; }

        /// <summary>조회·배치 INSERT·MERGE·트랜잭션 제어: 이력에 남기지 않는다.</summary>
        public ExecutionOptions BulkOptions { get; }

        public ICopyRunRecord? Record { get; set; }

        public DateTimeOffset ReadStartedAt { get; set; } = DateTimeOffset.Now;

        public Dictionary<string, TableState> Tables { get; } = new(StringComparer.Ordinal);

        public List<DisabledObject> Disabled { get; } = [];

        public List<FailedRow> FailedRows { get; } = [];

        public List<string> Notices { get; } = [];

        /// <summary>전체를 멈춤(끄기 실패·ORA-01555·원본 준비 실패). 중지(<see cref="Stopped"/>)와 달리 결과는 실패다.</summary>
        public bool Aborted { get; private set; }

        public bool Stopped { get; set; }

        public bool CommitFailed { get; set; }

        public bool RolledBackAll { get; set; }

        /// <summary>「실패 행만 다시 넣기」 실행.</summary>
        public bool IsRetry { get; set; }

        /// <summary>「실패 행만 다시 넣기」의 입력 행. 확정되지 않은 표의 행은 끝낼 때 그대로 결과에 돌려준다.</summary>
        public IReadOnlyList<FailedRow> RetryInputs { get; set; } = [];

        /// <summary>끈 트리거·FK 중 다시 켜지 못한 것이 있음. 결과는 실패다(꺼진 채 남은 것을 사용자가 알아야 한다, 설계 4장).</summary>
        public bool EnableFailed { get; set; }

        /// <summary>대상 정리(DELETE) 뒤 커밋이 한 번이라도 성공함(다른 표의 DELETE 도 확정됨).</summary>
        public bool DeletesPersisted { get; set; }

        public TableState Table(CopyTablePlan plan)
        {
            if (!Tables.TryGetValue(plan.Rule.Table, out var t))
            {
                t = new TableState(plan.Rule.Table);
                Tables[plan.Rule.Table] = t;
            }

            return t;
        }

        public void Abort(string? notice)
        {
            Aborted = true;
            if (notice is not null)
            {
                Notices.Add(notice);
            }
        }

        public void Progress(string table, long rows, long? estimated, string phase) =>
            _progress?.Report(new CopyProgress(table, rows, estimated, phase));

        /// <summary>
        /// 대상 문장 결과를 받는다. 성공이면 true. 중지로 취소됐으면 중지 예외를 올리고, 정책 거부·확인 거절·실패면 표를 실패로 둔다(예외로 던지지 않는다).
        /// </summary>
        public static bool Accept(BulkResult r, TableState table, string what, CancellationToken ct)
        {
            if (r.Status == ExecutionStatus.Succeeded)
            {
                return true;
            }

            if (r.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            table.Fail(what + ": " + Describe(r));
            return false;
        }

        public CopyOutcome Outcome(bool faulted)
        {
            // 다시 켜지 못한 트리거·FK 가 있으면 중지했더라도 실패다(꺼진 채 남은 것을 결과에서 알아야 한다).
            if (EnableFailed)
            {
                return CopyOutcome.Failed;
            }

            if (Stopped)
            {
                return CopyOutcome.Stopped;
            }

            var results = Tables.Values.Select(t => t.ToResult().Outcome).ToList();
            if (faulted || Aborted || CommitFailed || RolledBackAll || EnableFailed || (results.Count > 0 && results.All(o => o == CopyOutcome.Failed)))
            {
                return CopyOutcome.Failed;
            }

            return results.Any(o => o is CopyOutcome.Failed or CopyOutcome.PartiallyFailed) ? CopyOutcome.PartiallyFailed : CopyOutcome.Succeeded;
        }

        public CopyRunResult Result() =>
            new(Outcome(faulted: false), [.. Tables.Values.Select(t => t.ToResult())], [.. FailedRows], [.. Notices], ReadStartedAt);
    }
}
