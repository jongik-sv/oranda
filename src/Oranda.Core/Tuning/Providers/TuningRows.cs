using System.Globalization;
using System.Text;
using Oranda.Core.Plans;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Core.Tuning.Providers;

/// <summary>
/// 튜닝 질의 행 → 튜닝 모델 조립(DBMS 무관). 공급자는 카탈로그 <c>tuning.*</c> 행을 읽어 이 함수에 넘긴다.
/// 열 순서는 카탈로그 계약(같은 id = 같은 열)이다.
/// </summary>
public static class TuningRows
{
    /// <summary>세션 모니터가 넘기는 sql_id 목록 상한(바인드 글 길이 4000 안).</summary>
    public const int MaxSqlIds = 200;

    /// <summary>gv$sql_shared_cursor 에서 사유가 아닌 열.</summary>
    private static readonly HashSet<string> SharedCursorKeyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "INST_ID", "SQL_ID", "ADDRESS", "CHILD_ADDRESS", "CHILD_NUMBER", "REASON", "CON_ID",
    };

    /// <summary>gv$sql_shared_cursor 행을 'Y' 인 열 하나당 사유 한 줄로 펼친다. reason 열(XML)이 있으면 그 자식의 상세로 붙인다.</summary>
    public static IReadOnlyList<SharedCursorReason> ToReasons(IReadOnlyList<string> names, IEnumerable<object?[]> rows)
    {
        var child = IndexOf(names, "CHILD_NUMBER");
        var reason = IndexOf(names, "REASON");
        var result = new List<SharedCursorReason>();
        foreach (var r in rows)
        {
            var number = child >= 0 ? Int(r[child]) ?? 0 : 0;
            var detail = reason >= 0 ? Str(r[reason]) : null;
            for (var i = 0; i < names.Count && i < r.Length; i++)
            {
                if (!SharedCursorKeyColumns.Contains(names[i]) && r[i] is string v && string.Equals(v, "Y", StringComparison.Ordinal))
                {
                    result.Add(new SharedCursorReason(number, names[i].ToUpperInvariant())
                    {
                        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail,
                    });
                }
            }
        }

        return [.. result.OrderBy(x => x.ChildNumber)];

        static int IndexOf(IReadOnlyList<string> names, string name)
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>endpoint_number 순 버킷의 막대 높이 = 앞 버킷과의 차(첫 버킷은 자기 값). 순서가 뒤집혀 음수가 되면 0.</summary>
    public static IReadOnlyList<HistogramBucket> ToBuckets(IEnumerable<(long Endpoint, string? Value)> rows)
    {
        var result = new List<HistogramBucket>();
        long previous = 0;
        foreach (var (endpoint, value) in rows)
        {
            result.Add(new HistogramBucket(endpoint, value, Math.Max(0, endpoint - previous)));
            previous = endpoint;
        }

        return result;
    }

    /// <summary>ASH 표본 수에 비율(0~1)을 붙인다.</summary>
    public static IReadOnlyList<AshWaitRow> ToAshRows(IEnumerable<(string Event, string? WaitClass, long Samples)> rows)
    {
        var list = rows.ToList();
        var total = list.Sum(r => r.Samples);
        return [.. list.Select(r => new AshWaitRow(r.Event, r.WaitClass, r.Samples) { Share = total > 0 ? (double)r.Samples / total : 0 })];
    }

    /// <summary>서버 경로에서 파일 이름만(유닉스·윈도 구분자 모두).</summary>
    public static string TraceFileName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var i = path.LastIndexOfAny(['/', '\\']);
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string CheckSqlId(string sqlId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlId);
        return sqlId.Trim();
    }

    /// <summary>
    /// dba_hist_sql_plan 줄(id, parent_id, depth, operation, options, object_owner, object_name, cost, cardinality, access, filter) → 계획.
    /// 글은 DBMS_XPLAN 처럼 들여쓴 표 한 장(비교 화면은 단계만 쓴다).
    /// </summary>
    public static ExecutionPlan ToAwrPlan(IEnumerable<object?[]> rows, long planHashValue)
    {
        var steps = rows.Select(r => new PlanStep(Int(r[0]) ?? 0, Int(r[1]), Int(r[2]) ?? 0, Str(r[3]) ?? string.Empty)
        {
            Options = Str(r[4]),
            ObjectOwner = Str(r[5]),
            ObjectName = Str(r[6]),
            Cost = Long(r[7]),
            EstimatedRows = Long(r[8]),
            AccessPredicates = Str(r[9]),
            FilterPredicates = Str(r[10]),
        }).OrderBy(p => p.Id).ToList();

        var text = new StringBuilder();
        text.Append("Plan hash value: ").AppendLine(planHashValue.ToString(CultureInfo.InvariantCulture));
        text.AppendLine("(AWR dba_hist_sql_plan)");
        foreach (var p in steps)
        {
            text.Append(p.Id.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append(" | ")
                .Append(' ', p.Depth).Append(p.Operation);
            if (p.Options is { Length: > 0 } o)
            {
                text.Append(' ').Append(o);
            }

            if (p.ObjectName is { Length: > 0 } n)
            {
                text.Append(" | ").Append(p.ObjectOwner is { Length: > 0 } owner ? owner + "." + n : n);
            }

            if (p.Cost is { } cost)
            {
                text.Append(" | cost ").Append(cost.ToString(CultureInfo.InvariantCulture));
            }

            text.AppendLine();
        }

        return new ExecutionPlan(steps, text.ToString()) { PlanHashValue = planHashValue };
    }

    /// <summary>sql_id 목록을 쉼표로 이은 글(중복·빈 값 제거, 상한 <see cref="MaxSqlIds"/>). 목록이 없거나 비면 null.</summary>
    public static string? SqlIdList(IReadOnlyList<string>? ids)
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
    public static SqlStatsSample ToCandidate(object?[] r) => new(Str(r[1]) ?? string.Empty)
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
    public static LiteralSqlGroup ToLiteralGroup(object?[] r)
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
    public static PlanFindingLine ToPlanFinding(object?[] r) => new(Int(r[0]) ?? 0, Str(r[1]) ?? string.Empty, Str(r[2]), Str(r[3]), Str(r[4]))
    {
        Cardinality = Long(r[5]),
        TableRows = Long(r[6]),
        TableBlocks = Long(r[7]),
    };

    /// <summary>gv$sql 합계 한 행. 자식 수가 0 이면(공유 풀에 없음) null.</summary>
    public static SqlStatsRow? ToSqlStats(object?[] r)
    {
        if ((Int(r[0]) ?? 0) == 0)
        {
            return null;
        }

        return new SqlStatsRow(Int(r[0]) ?? 0, Int(r[1]) ?? 0, Long(r[2]) ?? 0, Long(r[3]) ?? 0, Long(r[4]) ?? 0, Long(r[5]) ?? 0, Long(r[6]) ?? 0, Long(r[7]) ?? 0)
        {
            ApplicationWaitMicros = Long(r[8]) ?? 0,
            ConcurrencyWaitMicros = Long(r[9]) ?? 0,
            ClusterWaitMicros = Long(r[10]) ?? 0,
            UserIoWaitMicros = Long(r[11]) ?? 0,
            ParseCalls = Long(r[12]) ?? 0,
            Fetches = Long(r[13]) ?? 0,
            ParsingSchema = Str(r[14]),
            Module = Str(r[15]),
            LastActiveTime = Date(r[16]),
            FirstLoadTime = Str(r[17]),
        };
    }

    /// <summary>환경 행(kind·name·value). 모르는 kind 는 버린다.</summary>
    public static IReadOnlyList<TuningEnvRow> ToEnvRows(IEnumerable<(string? Kind, string? Name, string? Value)> rows)
    {
        var result = new List<TuningEnvRow>();
        foreach (var (kind, name, value) in rows)
        {
            TuningEnvKind? k = kind switch
            {
                "VERSION" => TuningEnvKind.Version,
                "PARAMETER" => TuningEnvKind.Parameter,
                "NLS" => TuningEnvKind.Nls,
                _ => null,
            };
            if (k is { } known && !string.IsNullOrEmpty(name))
            {
                result.Add(new TuningEnvRow(known, name, value));
            }
        }

        return result;
    }
}
