using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Tuning.Assistant;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.Tuning.Report;

/// <summary>
/// 튜닝 보고서 자료 모으기(TSK-06-07). 모든 조회는 <see cref="ITuningService"/>·<see cref="IMonitorService"/> 의 SELECT 다(DB 에 쓰지 않는다).
/// 절마다 따로 실패를 받아 그 절에 「수집 실패: 이유」 를 남기고 다음 절로 간다. 팩(Diagnostics)이 필요한 조회는 보유 선언이 없으면 부르지 않는다.
/// 취소하면 <see cref="OperationCanceledException"/> 을 그대로 던진다(파일은 만들지 않는다).
/// </summary>
public sealed class TuningReportCollector
{
    /// <summary>계획 절에 담는 자식 커서 수 상한(넘으면 실행 수 상위).</summary>
    public const int MaxPlans = 4;

    /// <summary>통계를 읽는 표 수 상한.</summary>
    public const int MaxTables = 8;

    /// <summary>히스토그램을 읽는 열 수 상한(보고서 전체).</summary>
    public const int MaxHistograms = 4;

    /// <summary>ASH 로 볼 지난 시간.</summary>
    public static readonly TimeSpan AshWindow = TimeSpan.FromHours(1);

    private readonly ITuningService _tuning;
    private readonly IMonitorService? _monitor;

    public TuningReportCollector(ITuningService tuning, IMonitorService? monitor)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        _tuning = tuning;
        _monitor = monitor;
    }

    /// <summary>대기 표본 사이 쉬기. 시험은 바로 돌아오게 바꾼다.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>지금 시각(ASH 구간 시작). 시험용.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>앱 버전(보고서 머리·꼬리).</summary>
    public string AppVersion { get; init; } = "0.1.0";

    /// <summary>보유 팩: 접속 선언과 서버 control_management_pack_access 중 낮은 것(<c>OracleMonitorService.EnsureLicense</c> 와 같은 규칙).</summary>
    public static LicensePack HeldPack(ISession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var held = s.Profile.LicensePack;
        if (ExecutionPolicy.ParseServerPackAccess(s.Server.ManagementPackAccess) is { } server && server < held)
        {
            held = server;
        }

        return held;
    }

    /// <summary>보고서 머리 정보.</summary>
    public ReportContext ContextOf(ISession s) => new(s.Profile.Name, s.Server.SessionUser ?? s.Profile.User, s.Profile.Environment, s.Server.Version)
    {
        DatabaseName = s.Server.DbUniqueName ?? s.Server.DatabaseName,
        IsRac = s.Server.IsRac,
        HeldPack = HeldPack(s),
        AppVersion = AppVersion,
        CreatedAt = DateTimeOffset.Now,
    };

    public async Task<TuningReportData> CollectAsync(ISession session, TuningReportOptions o, IProgress<TuningReportProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(o);
        var en = o.English;
        var sqlId = o.SqlId.Trim();
        var data = new TuningReportData(o, ContextOf(session));
        var finished = new List<string>();
        var total = o.Sections.Count;
        var done = 0;
        void Report(string current) => progress?.Report(new TuningReportProgress(done, total, current, [.. finished]));
        void Finish(TuningReportSection s)
        {
            if (o.Includes(s))
            {
                done++;
                finished.Add(TuningReportText.SectionTitle(s, en));
            }
        }

        bool Want(params TuningReportSection[] any) => any.Any(o.Includes);

        var defaultOwner = (session.Server.SessionUser ?? session.Profile.User)?.ToUpperInvariant();

        // 1. SQL 전문·바인드
        if (Want(TuningReportSection.SqlText, TuningReportSection.Candidates, TuningReportSection.Summary))
        {
            Report(L(en, "SQL 전문 읽는 중…", "Reading the SQL text…"));
            data = data with { SqlText = await SqlTextAsync(session, o, ct).ConfigureAwait(true) };
        }

        Finish(TuningReportSection.SqlText);

        // 2. 자식 커서·불일치 사유
        var needPlans = Want(TuningReportSection.Plans, TuningReportSection.Graph, TuningReportSection.Candidates, TuningReportSection.ObjectStats, TuningReportSection.Summary);
        if (needPlans || o.Includes(TuningReportSection.ChildCursors))
        {
            Report(L(en, "자식 커서 읽는 중…", "Reading child cursors…"));
            data = data with { Cursors = await CursorsAsync(session, sqlId, en, ct).ConfigureAwait(true) };
        }

        Finish(TuningReportSection.ChildCursors);

        // 3. 자식 커서별 실제 계획
        if (needPlans)
        {
            data = data with { Plans = await PlansAsync(session, sqlId, o, data.Cursors.Value?.Children, msg => Report(msg), ct).ConfigureAwait(true) };
        }

        Finish(TuningReportSection.Plans);
        Finish(TuningReportSection.Graph);
        var plans = data.Plans.Value?.Where(p => p.Plan is { Steps.Count: > 0 }).Select(p => p.Plan!).ToList() ?? [];

        // 4. 실행 통계
        if (Want(TuningReportSection.ExecStats, TuningReportSection.Summary))
        {
            Report(L(en, "실행 통계 읽는 중…", "Reading execution statistics…"));
            data = data with { ExecStats = await Guard(async () =>
            {
                var row = await _tuning.GetSqlStatsAsync(session, sqlId, ct).ConfigureAwait(true);
                return row is null
                    ? ReportPart<SqlStatsRow>.Fail(L(en, "공유 풀에 이 sql_id 의 커서가 없습니다(밀려났거나 아직 실행되지 않았습니다).", "The shared pool has no cursor for this sql_id (aged out or never run)."))
                    : ReportPart<SqlStatsRow>.Ok(row);
            }, en, ct).ConfigureAwait(true) };
        }

        Finish(TuningReportSection.ExecStats);

        // 5. 표·인덱스·열 통계
        if (Want(TuningReportSection.ObjectStats, TuningReportSection.Summary, TuningReportSection.Candidates))
        {
            data = data with { Objects = await ObjectsAsync(session, plans, defaultOwner, data.Plans, msg => Report(msg), en, ct).ConfigureAwait(true) };
        }

        Finish(TuningReportSection.ObjectStats);

        var held = data.Context.HeldPack;

        // 6. 대기 분해(팩 있으면 ASH, 없으면 gv$session 표본)
        if (o.Includes(TuningReportSection.Waits))
        {
            data = data with { Waits = await WaitsAsync(session, sqlId, o, held >= LicensePack.Diagnostics, msg => Report(msg), ct).ConfigureAwait(true) };
        }

        Finish(TuningReportSection.Waits);

        // 7. AWR 계획 이력(팩 선언 없으면 보내지 않는다)
        if (o.Includes(TuningReportSection.Awr))
        {
            if (held < LicensePack.Diagnostics)
            {
                data = data with { Awr = ReportPart<ReportAwr>.Exclude(TuningReportText.NoDiagnostics(en)) };
            }
            else
            {
                data = data with { Awr = await AwrAsync(session, sqlId, msg => Report(msg), en, ct).ConfigureAwait(true) };
            }
        }

        Finish(TuningReportSection.Awr);

        // 8. 인덱스·재작성 후보(규칙, DB 조회는 기존 인덱스 열뿐)
        if (Want(TuningReportSection.Candidates, TuningReportSection.Summary))
        {
            Report(L(en, "후보 만드는 중…", "Building candidates…"));
            data = data with { Candidates = await CandidatesAsync(session, plans.FirstOrDefault(), data.SqlText.Value?.Text, data.Objects.Value, defaultOwner, en, ct).ConfigureAwait(true) };
        }

        Finish(TuningReportSection.Candidates);

        // 9. 환경
        if (o.Includes(TuningReportSection.Environment))
        {
            Report(L(en, "환경 읽는 중…", "Reading the environment…"));
            data = data with { Environment = await Guard(async () =>
                ReportPart<IReadOnlyList<TuningEnvRow>>.Ok(await _tuning.GetTuningEnvironmentAsync(session, ct).ConfigureAwait(true)), en, ct).ConfigureAwait(true) };
        }

        Finish(TuningReportSection.Environment);
        Finish(TuningReportSection.Summary);
        Report(L(en, "HTML 만드는 중…", "Writing the HTML…"));
        return data;
    }

    private async Task<ReportPart<ReportSqlText>> SqlTextAsync(ISession s, TuningReportOptions o, CancellationToken ct)
    {
        var en = o.English;
        string? failure;
        if (_monitor is null)
        {
            failure = L(en, "SQL 모니터 서비스를 쓸 수 없습니다.", "The SQL monitor service is not available.");
        }
        else
        {
            try
            {
                var detail = await _monitor.GetSqlDetailAsync(s, new CursorRef(o.SqlId.Trim(), o.ChildNumber), ct).ConfigureAwait(true);
                var binds = o.IncludeBindValues ? detail.Binds : [.. detail.Binds.Select(b => b with { Value = null })];
                return ReportPart<ReportSqlText>.Ok(new ReportSqlText(detail.FullText, binds));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = Reason(ex, en);
            }
        }

        // 공유 풀에 없으면 편집기 문장으로 대신한다(바인드 캡처는 없다).
        return string.IsNullOrWhiteSpace(o.EditorText)
            ? ReportPart<ReportSqlText>.Fail(failure)
            : ReportPart<ReportSqlText>.Ok(new ReportSqlText(o.EditorText.Trim(), []) { FromEditor = true });
    }

    private async Task<ReportPart<ReportCursors>> CursorsAsync(ISession s, string sqlId, bool en, CancellationToken ct)
    {
        IReadOnlyList<ChildCursorRow> children;
        try
        {
            children = await _tuning.GetChildCursorsAsync(s, sqlId, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ReportPart<ReportCursors>.Fail(Reason(ex, en));
        }

        try
        {
            var reasons = children.Count == 0 ? [] : await _tuning.GetSharedCursorReasonsAsync(s, sqlId, ct).ConfigureAwait(true);
            return ReportPart<ReportCursors>.Ok(new ReportCursors(children, reasons));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ReportPart<ReportCursors>.Ok(new ReportCursors(children, []) { ReasonsFailure = Reason(ex, en) });
        }
    }

    /// <summary>계획을 받을 자식: 고른 자식, 아니면 실행 수 상위 <see cref="MaxPlans"/>(인스턴스별 같은 번호는 실행 많은 쪽 하나).</summary>
    internal static IReadOnlyList<ChildCursorRow> PlanTargets(IReadOnlyList<ChildCursorRow> children, int? childNumber)
    {
        var pool = childNumber is { } n ? children.Where(c => c.ChildNumber == n) : children;
        return [.. pool
            .GroupBy(c => (c.ChildNumber, c.PlanHashValue))
            .Select(g => g.OrderByDescending(c => c.Executions).First())
            .OrderByDescending(c => c.Executions).ThenBy(c => c.ChildNumber)
            .Take(MaxPlans)];
    }

    private async Task<ReportPart<IReadOnlyList<ReportChildPlan>>> PlansAsync(
        ISession s, string sqlId, TuningReportOptions o, IReadOnlyList<ChildCursorRow>? children, Action<string> report, CancellationToken ct)
    {
        var en = o.English;

        // 자식 목록을 못 읽었으면 고른 자식(없으면 0)만 시도한다.
        var targets = children is null
            ? [new ChildCursorRow(o.ChildNumber ?? 0, 0, false, false, true, 0, 0, 0)]
            : PlanTargets(children, o.ChildNumber);
        if (targets.Count == 0)
        {
            return ReportPart<IReadOnlyList<ReportChildPlan>>.Fail(L(en,
                "공유 풀에 이 sql_id 의 자식 커서가 없어 실제 계획을 받을 수 없습니다. 문장을 한 번 실행한 뒤 다시 만드세요.",
                "The shared pool has no child cursor for this sql_id, so there is no actual plan. Run the statement once and make the report again."));
        }

        var result = new List<ReportChildPlan>();
        for (var i = 0; i < targets.Count; i++)
        {
            var c = targets[i];
            report(string.Format(CultureInfo.InvariantCulture, L(en, "실제 계획 읽는 중… child {0} ({1} / {2})", "Reading the actual plan… child {0} ({1} / {2})"), c.ChildNumber, i + 1, targets.Count));
            try
            {
                var plan = await _tuning.GetChildPlanAsync(s, new CursorRef(sqlId, c.ChildNumber, c.InstanceId), TuningAssistantCommandHandler.PlanFormat, ct).ConfigureAwait(true);
                result.Add(new ReportChildPlan(c.ChildNumber, c.InstanceId, plan, plan.Steps.Count == 0 ? L(en, "계획 단계가 없습니다.", "The plan has no steps.") : null)
                {
                    Cursor = children is null ? null : c,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.Add(new ReportChildPlan(c.ChildNumber, c.InstanceId, null, Reason(ex, en)) { Cursor = children is null ? null : c });
            }
        }

        return result.All(p => p.Plan is null)
            ? ReportPart<IReadOnlyList<ReportChildPlan>>.Fail(result[0].Failure ?? string.Empty)
            : ReportPart<IReadOnlyList<ReportChildPlan>>.Ok(result);
    }

    /// <summary>계획에서 읽는 표(표 읽기 단계 + 인덱스 후보 대상 표), 처음 나온 순서, 상한 <see cref="MaxTables"/>.</summary>
    internal static IReadOnlyList<TableTarget> TablesOf(IReadOnlyList<CorePlan> plans, string? defaultOwner)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<TableTarget>();
        void Add(string? owner, string? table)
        {
            owner = string.IsNullOrWhiteSpace(owner) ? defaultOwner : owner.Trim();
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(table) || result.Count >= MaxTables)
            {
                return;
            }

            if (seen.Add((owner, table.Trim())))
            {
                result.Add(new TableTarget(owner, table.Trim()));
            }
        }

        foreach (var plan in plans)
        {
            foreach (var step in plan.Steps)
            {
                if (step.Operation.StartsWith("TABLE ACCESS", StringComparison.OrdinalIgnoreCase)
                    || step.Operation.StartsWith("MAT_VIEW ACCESS", StringComparison.OrdinalIgnoreCase))
                {
                    Add(step.ObjectOwner, step.ObjectName);
                }
            }

            foreach (var t in IndexAdvisor.TablesToCheck(plan, defaultOwner))
            {
                Add(t.Owner, t.Table);
            }
        }

        return result;
    }

    private async Task<ReportPart<IReadOnlyList<ReportObjectStats>>> ObjectsAsync(
        ISession s, List<CorePlan> plans, string? defaultOwner, ReportPart<IReadOnlyList<ReportChildPlan>> planPart, Action<string> report, bool en, CancellationToken ct)
    {
        if (plans.Count == 0)
        {
            return ReportPart<IReadOnlyList<ReportObjectStats>>.Fail(planPart.Failure is { } f
                ? L(en, "계획을 받지 못해 관련 표를 알 수 없습니다: ", "No plan, so the tables are unknown: ") + f
                : L(en, "계획을 받지 못해 관련 표를 알 수 없습니다.", "No plan, so the tables are unknown."));
        }

        var tables = TablesOf(plans, defaultOwner);
        var predicateColumns = PredicateColumns(plans);
        var result = new List<ReportObjectStats>();
        var histogramsLeft = MaxHistograms;
        for (var i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            report(string.Format(CultureInfo.InvariantCulture, L(en, "통계 읽는 중… {0}.{1} ({2} / {3})", "Reading statistics… {0}.{1} ({2} / {3})"), t.Owner, t.Table, i + 1, tables.Count));
            try
            {
                var table = await _tuning.GetTableStatsAsync(s, t.Owner, t.Table, ct).ConfigureAwait(true);
                var columns = await _tuning.GetColumnStatsAsync(s, t.Owner, t.Table, ct).ConfigureAwait(true);
                var indexes = await _tuning.GetIndexStatsAsync(s, t.Owner, t.Table, ct).ConfigureAwait(true);
                IReadOnlyList<IndexColumnsRow>? indexColumns;
                try
                {
                    indexColumns = await _tuning.GetIndexColumnsAsync(s, t.Owner, t.Table, ct).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    indexColumns = null; // 인덱스 열 구성만 「확인 못함」
                }

                var histograms = new List<(string, string, IReadOnlyList<HistogramBucket>)>();
                foreach (var col in columns.Where(c => !string.Equals(c.Histogram, "NONE", StringComparison.OrdinalIgnoreCase) && predicateColumns.Contains(c.ColumnName)))
                {
                    if (histogramsLeft <= 0)
                    {
                        break;
                    }

                    histograms.Add((col.ColumnName, col.Histogram, await _tuning.GetHistogramAsync(s, t.Owner, t.Table, col.ColumnName, ct).ConfigureAwait(true)));
                    histogramsLeft--;
                }

                result.Add(new ReportObjectStats(t.Owner, t.Table)
                {
                    Stats = table,
                    Columns = columns,
                    Indexes = indexes,
                    IndexColumns = indexColumns,
                    Histograms = histograms,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.Add(new ReportObjectStats(t.Owner, t.Table) { Failure = Reason(ex, en) });
            }
        }

        return ReportPart<IReadOnlyList<ReportObjectStats>>.Ok(result);
    }

    /// <summary>계획 술어에 나온 열 이름(대문자).</summary>
    internal static HashSet<string> PredicateColumns(IReadOnlyList<CorePlan> plans)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in plans.SelectMany(p => p.Steps))
        {
            foreach (var term in PlanPredicates.Parse(step.AccessPredicates, step.FilterPredicates))
            {
                set.Add(term.Column);
                if (term.OtherColumn is { } other)
                {
                    set.Add(other);
                }
            }
        }

        return set;
    }

    private async Task<ReportPart<ReportWaits>> WaitsAsync(ISession s, string sqlId, TuningReportOptions o, bool ash, Action<string> report, CancellationToken ct)
    {
        var en = o.English;
        if (ash)
        {
            report(L(en, "ASH 대기 읽는 중…", "Reading ASH waits…"));
            return await Guard(async () =>
            {
                var rows = await _tuning.GetAshWaitsAsync(s, sqlId, Now() - AshWindow, ct).ConfigureAwait(true);
                return ReportPart<ReportWaits>.Ok(new ReportWaits(WaitBreakdown.FromAsh(rows), true, (int)AshWindow.TotalSeconds));
            }, en, ct).ConfigureAwait(true);
        }

        var seconds = Math.Clamp(o.WaitSampleSeconds, 1, 300);
        var samples = new List<WaitSample>();
        for (var i = 0; i < seconds; i++)
        {
            report(string.Format(CultureInfo.InvariantCulture, L(en, "대기 분해 표본 모으는 중… {0} / {1} 초", "Sampling waits… {0} / {1} s"), i, seconds));
            try
            {
                samples.AddRange(await _tuning.SampleWaitsAsync(s, sqlId, ct).ConfigureAwait(true));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return ReportPart<ReportWaits>.Fail(Reason(ex, en));
            }

            if (i + 1 < seconds)
            {
                await Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(true);
            }
        }

        return ReportPart<ReportWaits>.Ok(new ReportWaits(WaitBreakdown.FromSamples(samples), false, seconds));
    }

    /// <summary>AWR 이력 + plan_hash 가 둘 이상이면 실행당 경과가 가장 빠른·느린 plan_hash 의 계획(dba_hist_sql_plan).</summary>
    private async Task<ReportPart<ReportAwr>> AwrAsync(ISession s, string sqlId, Action<string> report, bool en, CancellationToken ct)
    {
        report(L(en, "AWR 계획 이력 읽는 중…", "Reading AWR plan history…"));
        return await Guard(async () =>
        {
            var rows = await _tuning.GetAwrPlanHistoryAsync(s, sqlId, ct).ConfigureAwait(true);
            var plans = new List<ReportAwrPlan>();
            foreach (var hash in AwrComparePair(rows))
            {
                report(string.Format(CultureInfo.InvariantCulture, L(en, "AWR 계획 읽는 중… plan_hash {0}", "Reading the AWR plan… plan_hash {0}"), hash));
                try
                {
                    plans.Add(new ReportAwrPlan(hash, await _tuning.GetAwrPlanAsync(s, sqlId, hash, ct).ConfigureAwait(true), null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    plans.Add(new ReportAwrPlan(hash, null, Reason(ex, en)));
                }
            }

            return ReportPart<ReportAwr>.Ok(new ReportAwr(rows, plans));
        }, en, ct).ConfigureAwait(true);
    }

    /// <summary>비교할 plan_hash 두 개: 실행당 경과가 가장 빠른 것과 가장 느린 것. 하나뿐이면 비어 있다.</summary>
    internal static IReadOnlyList<long> AwrComparePair(IReadOnlyList<AwrPlanRow> rows)
    {
        var perHash = rows.GroupBy(r => r.PlanHashValue)
            .Select(g => (Hash: g.Key, PerExec: (double)g.Sum(r => r.ElapsedMicros) / Math.Max(g.Sum(r => r.Executions), 1)))
            .OrderBy(x => x.PerExec).ThenBy(x => x.Hash).ToList();
        return perHash.Count < 2 ? [] : [perHash[0].Hash, perHash[^1].Hash];
    }

    private async Task<ReportPart<ReportCandidates>> CandidatesAsync(
        ISession s, CorePlan? plan, string? sql, IReadOnlyList<ReportObjectStats>? objects, string? defaultOwner, bool en, CancellationToken ct)
    {
        if (plan is null && string.IsNullOrWhiteSpace(sql))
        {
            return ReportPart<ReportCandidates>.Fail(L(en, "SQL 전문과 계획을 받지 못해 후보를 만들 수 없습니다.", "No SQL text and no plan, so no candidates."));
        }

        try
        {
            IReadOnlyList<IndexAdvice> indexes = [];
            if (plan is not null)
            {
                var existing = new Dictionary<(string, string), IReadOnlyList<IndexColumnsRow>?>();
                foreach (var o in objects ?? [])
                {
                    existing[(o.Owner, o.Table)] = o.IndexColumns;
                }

                foreach (var t in IndexAdvisor.TablesToCheck(plan, defaultOwner))
                {
                    if (!existing.ContainsKey((t.Owner, t.Table)))
                    {
                        try
                        {
                            existing[(t.Owner, t.Table)] = await _tuning.GetIndexColumnsAsync(s, t.Owner, t.Table, ct).ConfigureAwait(true);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                        {
                            existing[(t.Owner, t.Table)] = null;
                        }
                    }
                }

                indexes = IndexAdvisor.Suggest(plan, (o, t) => existing.GetValueOrDefault((o, t)), defaultOwner, en);
            }

            var rewrites = string.IsNullOrWhiteSpace(sql) ? [] : RewriteAdvisor.Analyze(sql, plan, en);
            return ReportPart<ReportCandidates>.Ok(new ReportCandidates(indexes, rewrites));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ReportPart<ReportCandidates>.Fail(Reason(ex, en));
        }
    }

    private static async Task<ReportPart<T>> Guard<T>(Func<Task<ReportPart<T>>> body, bool en, CancellationToken ct)
        where T : class
    {
        try
        {
            return await body().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ReportPart<T>.Fail(Reason(ex, en));
        }
    }

    /// <summary>예외 → 절에 적을 한 줄 이유.</summary>
    internal static string Reason(Exception ex, bool en)
    {
        if (ex is NotSupportedException && string.IsNullOrWhiteSpace(ex.Message) is false && ex.Message != new NotSupportedException().Message)
        {
            return FirstLine(ex.Message);
        }

        return ex switch
        {
            NotSupportedException => L(en, "이 접속에서는 이 조회를 지원하지 않습니다.", "This connection does not support this query."),
            OperationCanceledException => L(en, "시간이 지나 멈췄습니다.", "Timed out."),
            _ => FirstLine(ex.Message),
        };
    }

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static string L(bool en, string ko, string english) => TuningReportText.L(en, ko, english);
}
