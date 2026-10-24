using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.SqlPlus;

namespace Oranda.Oracle.Tuning;

/// <summary>
/// 튜닝 조회(PRD 11장, TSK-06-02). 조회는 카탈로그 <c>tuning.*</c> 를 세션 연결로 직접 읽는다(<see cref="OracleMonitorService"/> 와 같은 경로:
/// <c>@PFX@</c> = 딕셔너리 권한이면 dba, 아니면 all · 세션마다 한 번에 한 문장 · 팩이 필요한 쿼리는 보유 선언과 대조해 모자라면 보내지 않는다).
/// <para>
/// 모든 카탈로그 쿼리는 SELECT 다. 벤치마크만 사용자 문장을 실행하는데, SELECT/WITH 한 문장만 받아 <see cref="IExecutionService"/>(정책 관문)로 보낸다.
/// 자식 계획은 <see cref="IExecutionPlanService"/>(plan.cursor_steps) 를 그대로 부른다.
/// </para>
/// </summary>
[OrandaService(typeof(ITuningService))]
internal sealed partial class OracleTuningService : ITuningService
{
    /// <summary>벤치마크 반복 상한(실수로 큰 값을 넣어 운영 DB 를 오래 두드리지 않게).</summary>
    internal const int MaxIterations = 100;

    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    private static readonly ConditionalWeakTable<OracleSession, SemaphoreSlim> Gates = new();

    /// <summary>gv$sql_shared_cursor 에서 사유가 아닌 열.</summary>
    private static readonly HashSet<string> SharedCursorKeyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "INST_ID", "SQL_ID", "ADDRESS", "CHILD_ADDRESS", "CHILD_NUMBER", "REASON", "CON_ID",
    };

    private readonly IExecutionService _execution;
    private readonly IExecutionPlanService _plans;
    private readonly ILanguageService _language;

    public OracleTuningService(IExecutionService execution, IExecutionPlanService plans, ILanguageService language)
    {
        _execution = execution;
        _plans = plans;
        _language = language;
    }

    public async Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.table_stats", c => BindTable(c, owner, table), ct)).Rows;
        if (rows.Count == 0)
        {
            return null;
        }

        var r = rows[0];
        var lastAnalyzed = Date(r[5]);
        if (lastAnalyzed is null && Long(r[2]) is null)
        {
            return null; // 표는 있으나 통계를 모은 적이 없다.
        }

        return new TableStatsRow(Str(r[0]) ?? owner, Str(r[1]) ?? table, Long(r[2]), Long(r[3]), Long(r[4]), lastAnalyzed, Yes(r[6], "YES"));
    }

    public async Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.column_stats", c => BindTable(c, owner, table), ct)).Rows;
        return [.. rows.Select(r => new ColumnStatsRow(
            Str(r[0]) ?? string.Empty, Long(r[1]), Long(r[2]), Double(r[3]), Str(r[4]) ?? "NONE", Long(r[5]), Date(r[6])))];
    }

    public async Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        var rows = (await QueryAsync(s, "tuning.histogram", c =>
        {
            BindTable(c, owner, table);
            AddText(c, "column_name", column);
        }, ct)).Rows;
        return ToBuckets(rows.Select(r => (Long(r[0]) ?? 0, Str(r[1]) ?? Str(r[2]))));
    }

    public async Task<IReadOnlyList<IndexStatsRow>> GetIndexStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.index_stats", c => BindTable(c, owner, table), ct)).Rows;
        return [.. rows.Select(r => new IndexStatsRow(
            Str(r[0]) ?? string.Empty, Str(r[1]) ?? string.Empty, Long(r[2]), Long(r[3]), Long(r[4]), Int(r[5]), Date(r[6]), Yes(r[7], "YES")))];
    }

    public async Task<IReadOnlyList<ChildCursorRow>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.child_cursors", c => AddText(c, "sql_id", CheckSqlId(sqlId)), ct)).Rows;
        return [.. rows.Select(r => new ChildCursorRow(
            Int(r[1]) ?? 0, Long(r[2]) ?? 0, Yes(r[3], "Y"), Yes(r[4], "Y"), Yes(r[5], "Y"), Long(r[6]) ?? 0, Long(r[7]) ?? 0, Long(r[8]) ?? 0)
        {
            InstanceId = Int(r[0]),
        })];
    }

    public async Task<IReadOnlyList<SharedCursorReason>> GetSharedCursorReasonsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var result = await QueryAsync(s, "tuning.shared_cursor", c => AddText(c, "sql_id", CheckSqlId(sqlId)), ct);
        return ToReasons(result.Names, result.Rows);
    }

    public Task<ExecutionPlan> GetChildPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.ChildNumber is null)
        {
            throw new ArgumentException("자식 커서 번호(ChildNumber)가 필요합니다.", nameof(cursor));
        }

        return _plans.GetCursorPlanAsync(s, cursor, format, ct);
    }

    public async Task<IReadOnlyList<WaitSample>> SampleWaitsAsync(ISession s, string? sqlId, CancellationToken ct)
    {
        var id = sqlId is null ? null : CheckSqlId(sqlId);
        var at = DateTime.Now;
        var rows = (await QueryAsync(s, "tuning.wait_sample", c => AddText(c, "sql_id", id), ct)).Rows;
        return [.. rows.Select(r => new WaitSample(at, Long(r[1]) ?? 0, Str(r[2]), Str(r[3]), Str(r[4])))];
    }

    public async Task<TraceFileInfo> GetTraceFileAsync(ISession s, SessionKey target, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.trace_file", c =>
        {
            AddInt(c, "inst_id", target.InstanceId);
            AddLong(c, "sid", target.Sid);
            AddLong(c, "serial", target.Serial);
        }, ct)).Rows;
        var path = rows.Count == 0 ? null : Str(rows[0][0]);
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException(
                $"세션의 트레이스 파일을 찾을 수 없습니다(이미 끝났을 수 있습니다): SID {target.Sid}, SERIAL# {target.Serial}, 인스턴스 {target.InstanceId}.");
        }

        var reason = TraceUnavailableReason(s.Server);
        return new TraceFileInfo(path, reason is null, reason);
    }

    public async Task<IReadOnlyList<string>> ReadTraceLinesAsync(ISession s, TraceFileInfo file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(file);
        if (TraceUnavailableReason(s.Server) is { } reason)
        {
            throw new NotSupportedException(reason);
        }

        if (!file.Readable)
        {
            throw new NotSupportedException(file.UnavailableReason ?? "이 트레이스 파일은 읽을 수 없습니다.");
        }

        var rows = (await QueryAsync(s, "tuning.trace_lines", c => AddText(c, "file_name", TraceFileName(file.Path)), ct)).Rows;
        return [.. rows.Select(r => (Str(r[0]) ?? string.Empty).TrimEnd('\r', '\n'))];
    }

    public async Task<IReadOnlyList<IndexColumnsRow>> GetIndexColumnsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.index_columns", c => BindTable(c, owner, table), ct)).Rows;
        return [.. rows
            .GroupBy(r => Str(r[0]) ?? string.Empty, StringComparer.Ordinal)
            .Select(g => new IndexColumnsRow(g.Key, [.. g.OrderBy(r => Long(r[2]) ?? 0).Select(r => Str(r[1]) ?? string.Empty)]))];
    }

    public async Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);
        var stmt = BenchmarkStatement(_language, benchmark.Sql);

        var elapsed = TimeSpan.Zero;
        long logical = 0, physical = 0, rows = 0;
        long? planHash = null;
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            var before = await SessionStatistics.CaptureAsync(_execution, s, ct);

            var sw = Stopwatch.StartNew();
            var result = await _execution.ExecuteAsync(s, stmt, new Dictionary<string, BindValue>(),
                new ExecutionOptions { Source = HistorySource.Script, SkipHistory = i > 0 }, ct);
            rows = await DrainAsync(result, ct);
            sw.Stop();
            elapsed += sw.Elapsed;

            // 첫 회만: 직전 문장(방금 끝낸 벤치마크 문장)의 계획 해시. v$ 고정 뷰라 논리 읽기는 거의 없다.
            if (i == 0)
            {
                planHash = await PreviousPlanHashAsync(s, ct);
            }

            var after = await SessionStatistics.CaptureAsync(_execution, s, ct);
            if (before is not null && after is not null)
            {
                var diff = SessionStatistics.Diff(before, after);
                logical += Stat(diff, "session logical reads");
                physical += Stat(diff, "physical reads");
            }
        }

        return new BenchmarkResult(benchmark, iterations, elapsed / iterations, logical / iterations, physical / iterations, rows, planHash);

        static long Stat(IReadOnlyDictionary<string, decimal> diff, string name) =>
            diff.TryGetValue(name, out var v) ? (long)v : 0;
    }

    public async Task<IReadOnlyList<AwrPlanRow>> GetAwrPlanHistoryAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.awr_plan_history", c => AddText(c, "sql_id", CheckSqlId(sqlId)), ct)).Rows;
        return [.. rows.Select(r => new AwrPlanRow(
            Long(r[0]) ?? 0, Date(r[1]) ?? DateTime.MinValue, Long(r[2]) ?? 0, Long(r[3]) ?? 0, Long(r[4]) ?? 0, Long(r[5]) ?? 0))];
    }

    public async Task<IReadOnlyList<AshWaitRow>> GetAshWaitsAsync(ISession s, string sqlId, DateTime since, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.ash_waits", c =>
        {
            AddText(c, "sql_id", CheckSqlId(sqlId));
            c.Parameters.Add(new OracleParameter("since", OracleDbType.Date) { Value = since });
        }, ct)).Rows;
        return ToAshRows(rows.Select(r => (Str(r[0]) ?? "ON CPU", Str(r[1]), Long(r[2]) ?? 0)));
    }

    public async Task<IReadOnlyList<SqlPlanControlRow>> GetSqlPlanControlsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var id = CheckSqlId(sqlId);
        List<object?[]> rows;
        try
        {
            rows = (await QueryAsync(s, "tuning.plan_controls", c => AddText(c, "sql_id", id), ct)).Rows;
        }
        catch (OracleException ex) when (ex.Number is 942 or 1031)
        {
            return []; // dba_sql_* 를 볼 권한이 없다(계약: 빈 목록).
        }

        return [.. rows.Select(r => new SqlPlanControlRow(
            Str(r[0]) switch
            {
                "PROFILE" => SqlPlanControlKind.Profile,
                "BASELINE" => SqlPlanControlKind.Baseline,
                _ => SqlPlanControlKind.Patch,
            },
            Str(r[1]) ?? string.Empty, Str(r[2]) ?? string.Empty, Date(r[3]) ?? DateTime.MinValue, Str(r[4]))
        {
            Accepted = Str(r[5]) is { } a ? string.Equals(a, "YES", StringComparison.OrdinalIgnoreCase) : null,
        })];
    }

    /// <summary>
    /// 벤치마크로 보낼 문장: 한 문장, SELECT/WITH 질의, FOR UPDATE 없음(잠금), 바인드·치환 변수 없음(값을 받지 않는다).
    /// 분류는 언어 서비스(StatementClassifier)가 한다. 아니면 <see cref="ArgumentException"/> — 아무것도 보내지 않는다.
    /// </summary>
    internal static SqlStatement BenchmarkStatement(ILanguageService language, string sql)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("벤치마크할 문장이 비었습니다.", nameof(sql));
        }

        var stmts = language.Split(sql, SplitOptions.Default);
        if (stmts.Count != 1)
        {
            throw new ArgumentException($"벤치마크는 문장 하나만 받습니다(받은 문장 {stmts.Count}개).", nameof(sql));
        }

        var stmt = stmts[0];
        if (stmt.Kind != StatementKind.Query || stmt.IsClassificationUncertain || stmt.Verb is not ("SELECT" or "WITH"))
        {
            throw new ArgumentException($"벤치마크는 SELECT(WITH) 질의만 실행합니다: {stmt.Verb ?? stmt.Kind.ToString()}", nameof(sql));
        }

        if (ForUpdate().IsMatch(stmt.Text))
        {
            throw new ArgumentException("SELECT … FOR UPDATE 는 행을 잠가 벤치마크하지 않습니다.", nameof(sql));
        }

        if (stmt.BindNames.Count > 0 || stmt.SubstitutionNames.Count > 0)
        {
            throw new ArgumentException("바인드·치환 변수가 있는 문장은 벤치마크하지 않습니다(값을 글자로 넣어 주세요).", nameof(sql));
        }

        return stmt;
    }

    /// <summary>gv$sql_shared_cursor 행을 'Y' 인 열 하나당 사유 한 줄로 펼친다. reason 열(XML)이 있으면 그 자식의 상세로 붙인다.</summary>
    internal static IReadOnlyList<SharedCursorReason> ToReasons(IReadOnlyList<string> names, IEnumerable<object?[]> rows)
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
    internal static IReadOnlyList<HistogramBucket> ToBuckets(IEnumerable<(long Endpoint, string? Value)> rows)
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
    internal static IReadOnlyList<AshWaitRow> ToAshRows(IEnumerable<(string Event, string? WaitClass, long Samples)> rows)
    {
        var list = rows.ToList();
        var total = list.Sum(r => r.Samples);
        return [.. list.Select(r => new AshWaitRow(r.Event, r.WaitClass, r.Samples) { Share = total > 0 ? (double)r.Samples / total : 0 })];
    }

    /// <summary>트레이스 본문을 SQL 로 못 읽는 이유(12.2 미만). 읽을 수 있으면 null.</summary>
    internal static string? TraceUnavailableReason(ServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var parts = server.Version.Split('.');
        var major = server.MajorVersion;
        var minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
        if (major > 12 || (major == 12 && minor >= 2))
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"서버 {server.Version} 에서는 트레이스 본문을 SQL 로 읽을 수 없습니다(v$diag_trace_file_contents 는 12.2 이후). 서버의 파일을 직접 여세요.");
    }

    /// <summary>서버 경로에서 파일 이름만(유닉스·윈도 구분자 모두).</summary>
    internal static string TraceFileName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var i = path.LastIndexOfAny(['/', '\\']);
        return i < 0 ? path : path[(i + 1)..];
    }

    private static async Task<long?> PreviousPlanHashAsync(ISession s, CancellationToken ct)
    {
        try
        {
            var rows = (await QueryAsync(s, "tuning.prev_cursor", _ => { }, ct)).Rows;
            return rows.Count == 0 ? null : Long(rows[0][2]);
        }
        catch (OracleException)
        {
            return null; // v$session 을 볼 권한이 없으면 계획 해시는 모른다.
        }
    }

    private static async Task<long> DrainAsync(ExecutionResult result, CancellationToken ct)
    {
        switch (result.Status)
        {
            case ExecutionStatus.Succeeded:
                break;
            case ExecutionStatus.Cancelled:
                throw new OperationCanceledException("벤치마크를 취소했습니다.");
            case ExecutionStatus.Denied:
                throw new UnauthorizedAccessException(result.Policy?.Message ?? "정책 관문이 실행을 막았습니다.");
            default:
                throw new InvalidOperationException(result.Error?.Message ?? "실행하지 못했습니다.");
        }

        if (result.ResultSet is null)
        {
            return result.RowsAffected ?? 0;
        }

        await using var rs = result.ResultSet;
        long rows = 0;
        for (var page = 0; ; page++)
        {
            var p = await rs.GetPageAsync(page, ct);
            rows += p.RowCount;
            if (p.IsLast || p.RowCount == 0)
            {
                break;
            }
        }

        return rows;
    }

    /// <summary>카탈로그 <c>tuning.*</c> 를 세션 연결로 읽는다(열 이름 포함). 팩이 모자라면 드라이버로 보내지 않는다.</summary>
    private static async Task<(List<string> Names, List<object?[]> Rows)> QueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var query = Catalog.Value.Get(id, s.Server);
        OracleMonitorService.EnsureLicense(s, query.Header.License);
        var sql = query.Sql.Replace("@PFX@", s.Server.Capabilities.CanQueryDictionary ? "dba" : "all", StringComparison.Ordinal);

        var gate = Gates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLOBFetchSize = -1;
            cmd.CommandText = sql;
            bind(cmd);

            var rows = new List<object?[]>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var names = new List<string>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                names.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return (names, rows);
        }
        catch (OracleException ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string CheckSqlId(string sqlId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlId);
        return sqlId.Trim();
    }

    /// <summary>소유자·표 이름은 딕셔너리 표기 그대로(보통 대문자) 묶는다.</summary>
    private static void BindTable(OracleCommand c, string owner, string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        AddText(c, "owner", owner);
        AddText(c, "table_name", table);
    }

    private static void AddText(OracleCommand c, string name, string? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Varchar2)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim(),
        });

    private static void AddInt(OracleCommand c, string name, int value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int32) { Value = value });

    private static void AddLong(OracleCommand c, string name, long value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int64) { Value = value });

    [GeneratedRegex(@"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForUpdate();

    private static bool Yes(object? v, string yes) => string.Equals(Str(v), yes, StringComparison.OrdinalIgnoreCase);

    private static string? Str(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    private static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static long? Long(object? v) => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static double? Double(object? v) => v is null ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    private static DateTime? Date(object? v) => v is DateTime d ? d : null;
}
