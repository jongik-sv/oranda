using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.Oracle.Tuning;

/// <summary>
/// 튜닝 추천 후보 조회(TSK-14-17, D-051). 카탈로그 <c>tuning.recommend_candidates</c>·<c>tuning.recommend_literals</c>·<c>tuning.recommend_plan</c>,
/// 모두 SELECT·팩 없음. 부하 제한: 후보는 gv$sqlstats 상위 N 줄만, 파싱 스키마는 그 줄에만, 계획은 sql_id·plan_hash 하나씩.
/// </summary>
internal sealed partial class OracleTuningService
{
    /// <summary>한 번에 읽는 후보 줄 상한.</summary>
    internal const int MaxCandidates = 500;

    /// <summary>세션 모니터가 넘기는 sql_id 목록 상한(바인드 글 길이 4000 안).</summary>
    internal const int MaxSqlIds = 200;

    public async Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var ids = SqlIdList(query.SqlIds);
        if (query.SqlIds is not null && ids is null)
        {
            return new TuningCandidateSet([], 0);
        }

        var rows = (await QueryAsync(s, "tuning.recommend_candidates", c =>
        {
            AddInt(c, "top", Math.Clamp(query.Top, 1, MaxCandidates));
            AddLong(c, "min_executions", Math.Max(0, query.MinExecutions));
            AddText(c, "sql_ids", ids);
        }, ct)).Rows;

        var samples = rows.Select(ToCandidate).ToList();
        var total = rows.Count > 0 && ids is null ? Long(rows[0][18]) ?? 0 : 0; // sql_id 로 좁힌 조회는 비중 분모가 뜻이 없다
        IReadOnlyList<LiteralSqlGroup> groups = [];
        if (query.IncludeLiteralGroups && ids is null)
        {
            var g = (await QueryAsync(s, "tuning.recommend_literals", c =>
            {
                AddInt(c, "min_count", TuningRecommendationRules.LiteralGroupMinCount);
                AddInt(c, "top", TuningRecommendationRules.LiteralGroupMaxCount);
            }, ct)).Rows;
            groups = [.. g.Select(ToLiteralGroup)];
        }

        return new TuningCandidateSet(samples, total) { LiteralGroups = groups };
    }

    public async Task<IReadOnlyList<PlanFindingLine>> GetPlanFindingsAsync(ISession s, int? instanceId, string sqlId, long planHashValue, CancellationToken ct)
    {
        var id = CheckSqlId(sqlId);
        var rows = (await QueryAsync(s, "tuning.recommend_plan", c =>
        {
            AddInt(c, "inst_id", instanceId ?? 1);
            AddText(c, "sql_id", id);
            AddLong(c, "plan_hash", planHashValue);
        }, ct)).Rows;
        return [.. rows.Select(ToPlanFinding)];
    }

    /// <summary>sql_id 목록을 쉼표로 이은 글(중복·빈 값 제거, 상한 <see cref="MaxSqlIds"/>). 목록이 없거나 비면 null.</summary>
    internal static string? SqlIdList(IReadOnlyList<string>? ids)
    {
        if (ids is null)
        {
            return null;
        }

        var clean = ids.Where(i => !string.IsNullOrWhiteSpace(i) && i.Trim().All(char.IsLetterOrDigit))
            .Select(i => i.Trim()).Distinct(StringComparer.Ordinal).Take(MaxSqlIds).ToList();
        return clean.Count == 0 ? null : string.Join(',', clean);
    }

    /// <summary>후보 한 줄(열 순서 = recommend_candidates.sql).</summary>
    internal static SqlStatsSample ToCandidate(object?[] r) => new(Str(r[1]) ?? string.Empty)
    {
        InstanceId = Int(r[0]),
        PlanHashValue = Long(r[2]) ?? 0,
        SqlText = Str(r[3]) ?? string.Empty,
        Executions = Long(r[4]) ?? 0,
        ElapsedMicros = Long(r[5]) ?? 0,
        CpuMicros = Long(r[6]) ?? 0,
        BufferGets = Long(r[7]) ?? 0,
        DiskReads = Long(r[8]) ?? 0,
        RowsProcessed = Long(r[9]) ?? 0,
        ParseCalls = Long(r[10]) ?? 0,
        VersionCount = Long(r[11]) ?? 0,
        ForceMatchingSignature = Str(r[12]),
        UserIoWaitMicros = Long(r[13]) ?? 0,
        ApplicationWaitMicros = Long(r[14]) ?? 0,
        ConcurrencyWaitMicros = Long(r[15]) ?? 0,
        ClusterWaitMicros = Long(r[16]) ?? 0,
        LastActiveTime = Date(r[17]),
        ParsingSchema = Str(r[19]),
    };

    /// <summary>리터럴 묶음 한 줄(열 순서 = recommend_literals.sql).</summary>
    internal static LiteralSqlGroup ToLiteralGroup(object?[] r)
    {
        var fms = Str(r[0]) ?? "0";
        var count = Int(r[1]) ?? 0;
        var sample = new SqlStatsSample(Str(r[10]) ?? string.Empty)
        {
            Executions = Long(r[2]) ?? 0,
            ElapsedMicros = Long(r[3]) ?? 0,
            CpuMicros = Long(r[4]) ?? 0,
            BufferGets = Long(r[5]) ?? 0,
            DiskReads = Long(r[6]) ?? 0,
            RowsProcessed = Long(r[7]) ?? 0,
            ParseCalls = Long(r[8]) ?? 0,
            InstanceId = Int(r[9]),
            SqlText = Str(r[11]) ?? string.Empty,
            ParsingSchema = Str(r[12]),
            ForceMatchingSignature = fms,
            SimilarSqlCount = count,
            IsLiteralGroup = true,
        };
        return new LiteralSqlGroup(fms, count, sample);
    }

    /// <summary>계획 줄(열 순서 = recommend_plan.sql).</summary>
    internal static PlanFindingLine ToPlanFinding(object?[] r) => new(Int(r[0]) ?? 0, Str(r[1]) ?? string.Empty, Str(r[2]), Str(r[3]), Str(r[4]))
    {
        Cardinality = Long(r[5]),
        TableRows = Long(r[6]),
        TableBlocks = Long(r[7]),
    };
}
