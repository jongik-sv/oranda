using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.References;

namespace Oranda.Language.Ai.Tools;

public sealed partial class TuningToolbox
{
    /// <summary>줄이는 중에도 남기는 계획 지적 줄 수(1단계에서 이 수로, 2단계에서 0으로).</summary>
    private const int KeptPlanFindings = 3;

    private const string NoSqlViewPrivilege = "v$sql: 권한 없음";

    /// <summary>
    /// <c>get_tuning_context</c>: 문장이 참조하는 객체의 통계·쓰는 열·인덱스와 계획 지적 줄을 한 번에 묶는다.
    /// 응답이 <see cref="TuningContextRequest.BudgetTokens"/> 를 넘으면 계획 지적 줄 → 인덱스 통계 → 열 통계 순서로 줄이고 <c>omitted</c> 에 적는다.
    /// 객체 하나를 읽지 못해도 나머지는 돌려주며, 그 객체에는 이유를 적는다.
    /// </summary>
    public async Task<TuningContext> GetTuningContextAsync(ISession s, TuningContextRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Sql) && string.IsNullOrWhiteSpace(request.SqlId))
        {
            throw new AiToolException("get_tuning_context 에는 sql 또는 sql_id 가 필요합니다.");
        }

        var omitted = new List<string>();
        var findings = new List<ContextPlanFinding>();
        long? planHash = null;
        ExecutionPlan? plan = null;

        // 문장의 sql_id: 호출이 준 값, 없으면 문장 글에서 계산한 값이 공유 풀에 있을 때. 이 실행 범위의 스키마가 파싱한 커서만 쓴다.
        var sqlId = string.IsNullOrWhiteSpace(request.SqlId) ? null : request.SqlId;
        // V$SQL 조회 권한이 없으면(null) 그 부분만 빼고 omitted 에 적는다. 보이는지 알 수 없으므로 sql_id 는 쓰지 않는다.
        if (sqlId is not null)
        {
            var visible = await TryCursorVisibleAsync(s, sqlId, request.CursorSchema, ct).ConfigureAwait(false);
            if (visible is null)
            {
                omitted.Add(NoSqlViewPrivilege);
                sqlId = null;
            }
            else if (!visible.Value)
            {
                omitted.Add($"계획: {CursorNotVisible}");
                sqlId = null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Sql))
        {
            var computed = SqlIdCalculator.Compute(request.Sql);
            var visible = await TryCursorVisibleAsync(s, computed, request.CursorSchema, ct).ConfigureAwait(false);
            if (visible is null)
            {
                omitted.Add(NoSqlViewPrivilege);
            }
            else if (visible.Value)
            {
                sqlId = computed;
            }
        }

        if (sqlId is not null)
        {
            plan = await GetPlanQuietlyAsync(s, new CursorRef(sqlId), omitted, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(request.SqlId) && string.IsNullOrWhiteSpace(request.Sql))
        {
            // sql_id 만 받았는데 볼 수 없는 커서: 계획 없이 객체도 찾을 수 없다.
        }
        else if (request.AllowExplainPlan)
        {
            plan = await ExplainQuietlyAsync(s, request.Sql!, omitted, ct).ConfigureAwait(false);
        }
        else
        {
            omitted.Add(CursorPlanHint("계획: 이 접속은 EXPLAIN PLAN 을 허용하지 않습니다."));
        }

        if (plan is not null)
        {
            planHash = plan.PlanHashValue;
            findings.AddRange(PlanFindings(plan));
        }

        var objects = await BuildObjectsAsync(s, request, plan, omitted, ct).ConfigureAwait(false);
        omitted.Add("히스토그램 끝값(행 값 수준 아님)");
        var context = new TuningContext(
            new ContextStatement(sqlId, planHash, request.Sql?.Length ?? 0), objects, findings, omitted);
        return FitBudget(context, request.BudgetTokens);
    }

    private async Task<IReadOnlyList<ContextObject>> BuildObjectsAsync(
        ISession s, TuningContextRequest request, ExecutionPlan? plan, List<string> omitted, CancellationToken ct)
    {
        List<(ReferencedObject Written, IReadOnlyList<string> Used)> targets;
        StatementReferences? refs = null;
        if (!string.IsNullOrWhiteSpace(request.Sql))
        {
            refs = StatementReferenceExtractor.Extract(request.Sql);
            targets = [.. refs.Objects.Select(o => (o, refs.ColumnsOf(o)))];
        }
        else
        {
            targets = [.. (plan?.Steps ?? [])
                .Where(x => x.ObjectName is not null && x.Operation.StartsWith("TABLE ACCESS", StringComparison.Ordinal))
                .Select(x => new ReferencedObject(x.ObjectOwner, x.ObjectName!))
                .Distinct()
                .Select(o => (o, (IReadOnlyList<string>)[]))];
        }

        var resolved = await StatementReferenceExtractor
            .ResolveAsync(new StatementReferences([.. targets.Select(t => t.Written)], []), request.DefaultOwner, (o, n) => metadata.GetNameStepAsync(s, o, n, ct))
            .ConfigureAwait(false);
        var result = new List<ContextObject>();
        // 같은 객체가 별칭·시노님 때문에 두 번 풀리면 하나로 합치고 쓰는 열도 합친다.
        var used = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (var i = 0; i < resolved.Count; i++)
        {
            if (resolved[i].Target is { } t)
            {
                if (!used.TryGetValue(Name(t), out var set))
                {
                    used[Name(t)] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.UnionWith(targets[i].Used);
            }
        }

        var built = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in resolved)
        {
            if (r.Target is null)
            {
                result.Add(new ContextObject(r.Written.Display, "UNKNOWN", null, [], [], r.Problem));
                continue;
            }

            if (built.Add(Name(r.Target)))
            {
                result.Add(await BuildObjectAsync(s, r.Target, [.. used[Name(r.Target)]], omitted, ct).ConfigureAwait(false));
            }
        }

        return result;
    }

    private async Task<ContextObject> BuildObjectAsync(
        ISession s, DbObjectRef target, IReadOnlyList<string> usedNames, List<string> omitted, CancellationToken ct)
    {
        try
        {
            var describe = await DescribeObjectAsync(s, target, ct).ConfigureAwait(false);
            var stats = await GetTableStatsAsync(s, target, null, ct).ConfigureAwait(false);
            var indexes = await GetIndexInfoAsync(s, target, ct).ConfigureAwait(false);

            var wanted = usedNames.ToHashSet(StringComparer.Ordinal);
            foreach (var ix in indexes)
            {
                wanted.UnionWith(ix.Columns.Where(c => c.Expression is null).Select(c => c.Column));
            }

            var byName = stats.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var used = describe.Columns
                .Where(c => wanted.Contains(c.Name))
                .Select(c =>
                {
                    byName.TryGetValue(c.Name, out var cs);
                    return new ContextColumn(c.Name, c.Type, c.Nullable, c.Comment, cs?.NumDistinct, cs?.NumNulls, cs?.Histogram);
                })
                .ToList();
            var unused = describe.Columns.Count - used.Count;
            if (unused > 0)
            {
                omitted.Add($"{describe.Name} 의 쓰지 않는 열 {unused.ToString(CultureInfo.InvariantCulture)}개");
            }

            return new ContextObject(
                describe.Name,
                describe.Kind,
                stats.HasStats ? new ContextStats(stats.NumRows, stats.Blocks, stats.LastAnalyzed, stats.Stale) : null,
                used,
                [.. indexes.Select(ix => new ContextIndex(
                    ix.Name, ix.Unique, [.. ix.Columns.Select(IndexColumnText)], ix.Blevel, ix.LeafBlocks, ix.DistinctKeys, ix.ClusteringFactor, ix.Stale))],
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new ContextObject(Name(target), target.ObjectType, null, [], [], SafeReason("읽지 못했습니다", ex));
        }
    }

    private static string IndexColumnText(IndexColumnDescription c)
    {
        var text = c.Expression ?? c.Column;
        return c.Descending ? text + " DESC" : text;
    }

    private async Task<ExecutionPlan?> GetPlanQuietlyAsync(ISession s, CursorRef cursor, List<string> omitted, CancellationToken ct)
    {
        try
        {
            return await plans.GetCursorPlanAsync(s, cursor, "ALLSTATS LAST", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            omitted.Add(SafeReason("계획", ex));
            return null;
        }
    }

    private async Task<ExecutionPlan?> ExplainQuietlyAsync(ISession s, string sql, List<string> omitted, CancellationToken ct)
    {
        try
        {
            return await plans.ExplainAsync(s, ExplainableStatement(sql), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            omitted.Add(SafeReason("계획", ex));
            return null;
        }
    }

    /// <summary>눈여겨볼 계획 단계: 위험 표지가 있거나 추정 행과 실제 행이 10배 넘게 다른 단계.</summary>
    private static IEnumerable<ContextPlanFinding> PlanFindings(ExecutionPlan plan)
    {
        foreach (var x in plan.Steps)
        {
            // 추정 행(E-Rows)은 시작 한 번당 값이라 실제 행(A-Rows)과는 E-Rows × Starts 로 견준다. 실제 0행은 지적하지 않는다.
            var expected = x.EstimatedRows * Math.Max(1, x.Starts ?? 1);
            var misestimated = expected is > 0 && x.ActualRows is > 0 and var a
                && (a >= expected * 10 || a * 10 <= expected);
            if (x.Risk == PlanRisk.None && !misestimated)
            {
                continue;
            }

            var note = x.ActualRows is null
                ? $"E-Rows {Count(x.EstimatedRows)}"
                : $"E-Rows {Count(x.EstimatedRows)} / A-Rows {Count(x.ActualRows)}";
            var op = string.IsNullOrEmpty(x.Options) ? x.Operation : x.Operation + " " + x.Options;
            var objectName = x.ObjectName;
            yield return new ContextPlanFinding(x.Id, op, objectName, note);
        }
    }

    /// <summary>
    /// 읽지 못한 이유를 LLM 에게 돌려줄 글. 우리가 만든 안내(<see cref="AiToolException"/>)는 그대로, 서버 오류는 오류 번호만 쓴다
    /// (서버 원문에는 값이 들어갈 수 있다).
    /// </summary>
    private static string SafeReason(string what, Exception ex)
    {
        if (ex is AiToolException)
        {
            return $"{what}: {ex.Message}";
        }

        var code = System.Text.RegularExpressions.Regex.Match(ex.Message, @"ORA-\d{5}").Value;
        return code.Length > 0 ? $"{what}: 읽지 못했습니다({code})" : $"{what}: 읽지 못했습니다";
    }

    private static string Count(long? n) => n?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";

    private static TuningContext FitBudget(TuningContext context, int budgetTokens)
    {
        if (budgetTokens <= 0 || ToolJson.EstimateTokens(context) <= budgetTokens)
        {
            return context;
        }

        var omitted = context.Omitted.ToList();
        var current = context;

        // 1단계: 계획 지적 줄을 KeptPlanFindings 개로, 2단계: 0개로.
        foreach (var keep in new[] { KeptPlanFindings, 0 })
        {
            if (current.PlanFindings.Count > keep)
            {
                omitted.Add($"계획 지적 줄 {(current.PlanFindings.Count - keep).ToString(CultureInfo.InvariantCulture)}개");
                current = current with { PlanFindings = [.. current.PlanFindings.Take(keep)], Omitted = omitted };
            }

            if (ToolJson.EstimateTokens(current) <= budgetTokens)
            {
                return current;
            }
        }

        // 3단계: 인덱스 통계(이름·열 구성은 남긴다).
        omitted.Add("인덱스 통계(blevel·leaf_blocks·distinct_keys·clustering_factor)");
        current = current with
        {
            Objects = [.. current.Objects.Select(o => o with
            {
                Indexes = [.. o.Indexes.Select(i => i with { Blevel = null, LeafBlocks = null, DistinctKeys = null, ClusteringFactor = null, Stale = null })],
            })],
            Omitted = omitted,
        };
        if (ToolJson.EstimateTokens(current) <= budgetTokens)
        {
            return current;
        }

        // 4단계: 열 통계(열 이름·형·주석은 남긴다).
        omitted.Add("열 통계(num_distinct·num_nulls·histogram)");
        current = current with
        {
            Objects = [.. current.Objects.Select(o => o with
            {
                UsedColumns = [.. o.UsedColumns.Select(c => c with { NumDistinct = null, NumNulls = null, Histogram = null })],
            })],
            Omitted = omitted,
        };
        if (ToolJson.EstimateTokens(current) > budgetTokens)
        {
            omitted.Add("예산 초과: 더 줄일 항목이 없어 이 크기로 돌려줍니다. 필요한 객체만 describe_object 등으로 나눠 물으십시오.");
        }

        return current;
    }
}
