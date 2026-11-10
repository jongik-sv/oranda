using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Composition.Dispatch;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Oracle.Connection;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Schema;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 교차 복사(Oracle↔Tibero) 통합 시험 한 건의 틀(xdb-copy XC10). Oracle 시험 DB 와 Tibero 시험 서버를 한 시험에서 함께 쓴다.
/// <list type="bullet">
/// <item>관문: Tibero 쪽은 <see cref="TiberoItGate"/>(서버·드라이버·<c>ORANDA_IT_TIBERO_WRITE=1</c>), Oracle 쪽은 <c>ORANDA_IT_ADMIN_PASSWORD</c>
/// (선택: <c>ORANDA_IT_ADMIN_USER</c> 기본 SYSTEM, <c>ORANDA_IT_HOST</c> 기본 127.0.0.1, <c>ORANDA_IT_PORT</c> 기본 1521, <c>ORANDA_IT_SERVICE</c> 기본 FREEPDB1 — <c>ItDb</c> 와 같은 규칙).
/// 없으면 「건너뜀」, <c>ORANDA_IT_REQUIRE=1</c> 이면 실패한다(단, Tibero 쓰기 스위치 <c>ORANDA_IT_TIBERO_WRITE</c> 가 없을 때는 기존 Tibero 쓰기 IT 와 같이 건너뛴다).</item>
/// <item>Oracle 쪽 표는 시험마다 만드는 임시 사용자(<c>ORA_XC10_…</c>) 소유이고 끝에 사용자째 지운다. Tibero 쪽 표·시퀀스는 <c>ORT_</c> 이름만 쓰고
/// 처음과 끝에 <c>DROP … PURGE</c> 한다. 복사 표 이름은 두 쪽이 같다(복사는 같은 이름 표로 넣는다).</item>
/// <item>부품 구성: Tibero 의 <see cref="ICopyDbmsParts"/> 키 등록은 XC11 이 했지만 이 시험은 디스패처(<c>CopyRunnerDispatcher</c>)를 거치지 않고
/// <see cref="CopyRunner"/>·<see cref="CopyPrecheckService"/> 에 원본 벌·대상 벌을 직접 넣는다. 대량 실행·실행 엔진·메타데이터는 앱 조립처럼
/// 키 없는 디스패처(세션 DBMS 로 고름)에 두 DBMS 구현을 꽂은 <see cref="DbmsServiceTable"/> 을 준다. Oracle 구현은 internal 이라 형식 이름으로 만든다.</item>
/// <item>접속 정보(주소·계정·비밀번호·DB 이름)는 어디에도 찍지 않는다. 관찰 줄·단언 메시지는 <see cref="Scrub"/> 을 거친다.</item>
/// </list>
/// 사용: <c>await using var env = await CrossDbmsItEnvironment.CreateAsync(); if (env is null) return;</c>
/// </summary>
internal sealed class CrossDbmsItEnvironment : IAsyncDisposable
{
    /// <summary>
    /// 마지막 DDL 뒤 복사를 시작하기까지 기다리는 시간. 원본을 <c>SET TRANSACTION READ ONLY</c> 로 읽는데 DDL 시각→SCN 대응이 초 단위라
    /// 방금 만든 표는 몇 초 동안 읽지 못한다(Oracle ORA-01466, <c>CopyDbScope</c> 와 같은 값).
    /// </summary>
    public static readonly TimeSpan DdlSettle = TimeSpan.FromSeconds(3);

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private static readonly Assembly OracleAssembly = typeof(ConnectionProfileExtensions).Assembly;

    private readonly OracleAccount _oracle;
    private readonly ISessionManager _oracleManager;
    private readonly List<string> _tiberoTables = [];
    private readonly List<string> _tiberoSequences = [];
    private readonly Dictionary<Dbms, CopyDbmsParts> _parts = [];
    private TiberoSession? _tiberoSetup;
    private ISession? _oracleReader;
    private long _lastDdl;

    private CrossDbmsItEnvironment(TiberoItSessions tibero, OracleAccount oracle)
    {
        Tibero = tibero;
        _oracle = oracle;
        Policy = new AllowAll();
        var ui = new NoQuestions();
        var selector = new DbmsServiceTable();
        Bulk = new BulkExecutionServiceDispatcher(selector);
        Execution = new ExecutionServiceDispatcher(selector);
        Metadata = new MetadataProviderDispatcher(selector);

        var oracleBulk = NewOracle<IBulkExecutionService>("Oranda.Oracle.Execution.OracleBulkExecutionService", Policy, ui, Events);
        var oracleExecution = NewOracle<IExecutionService>("Oranda.Oracle.Execution.OracleExecutionService", Policy, ui, Events, null, 500, false, null);
        var oracleMetadata = NewOracle<IMetadataProvider>("Oranda.Oracle.Metadata.OracleMetadataProvider", new MemoryMetadataCache(), Events, Timeout.InfiniteTimeSpan);
        selector.Add(Dbms.Oracle, () => oracleBulk)
                .Add(Dbms.Oracle, () => oracleExecution)
                .Add(Dbms.Oracle, () => oracleMetadata)
                .Add<IBulkExecutionService>(Dbms.Tibero, () => new TiberoBulkExecutionService(Policy, ui, Events))
                .Add<IExecutionService>(Dbms.Tibero, () => new TiberoExecutionService(Policy, ui, Events, null, dbmsOutput: false))
                .Add<IMetadataProvider>(Dbms.Tibero, () => new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync));

        // 원본·대상 부품 벌(xdb-copy XC2): 앱 조립과 같이 「되살리기」 부품에는 키 없는 대량 실행(디스패처)을 준다.
        _parts[Dbms.Oracle] = NewOracle<ICopyDbmsParts>("Oranda.Oracle.Transfer.Copy.OracleCopyParts", Bulk).Parts;
        _parts[Dbms.Tibero] = new TiberoCopyParts(Bulk).Parts;
        _oracleManager = NewOracle<ISessionManager>("Oranda.Oracle.Sessions.OracleSessionManager", Events);
    }

    public TiberoItSessions Tibero { get; }

    public EventBus Events => Tibero.Events;

    public IExecutionPolicy Policy { get; }

    /// <summary>세션 DBMS 로 Oracle·Tibero 구현을 고르는 대량 실행(앱의 키 없는 <see cref="IBulkExecutionService"/> 자리).</summary>
    public IBulkExecutionService Bulk { get; }

    public IExecutionService Execution { get; }

    public IMetadataProvider Metadata { get; }

    /// <summary>Oracle 임시 사용자(복사 표 소유자) 이름. 관찰 줄에는 <see cref="Scrub"/> 으로 가린다.</summary>
    public string OracleUser => _oracle.User;

    /// <summary>
    /// 건너뛸 사유(없으면 null = 돈다). Tibero 쓰기 관문을 먼저 보고, Oracle 은 <c>ORANDA_IT_ADMIN_PASSWORD</c> 가 없을 때 건너뛴다.
    /// <c>ORANDA_IT_REQUIRE=1</c> 이면 Oracle 쪽은 건너뛰지 않는다(<see cref="CreateAsync"/> 가 던진다). 사유에는 변수 이름만 싣는다.
    /// </summary>
    public static string? SkipReason()
    {
        if (TiberoItGate.Reason(write: true, kill: false) is { } tibero)
        {
            return tibero;
        }

        if (Environment.GetEnvironmentVariable("ORANDA_IT_REQUIRE") == "1")
        {
            return null;
        }

        return string.IsNullOrEmpty(OracleEnv("ADMIN_PASSWORD")) ? "Oracle 시험 DB 환경 변수 ORANDA_IT_ADMIN_PASSWORD 가 없어 건너뜀." : null;
    }

    /// <summary>두 쪽 환경이 모두 있으면 틀을 만든다(Oracle 임시 사용자 생성 포함). 없으면 null, <c>ORANDA_IT_REQUIRE=1</c> 이면 던진다.</summary>
    public static async Task<CrossDbmsItEnvironment?> CreateAsync()
    {
        var require = Environment.GetEnvironmentVariable("ORANDA_IT_REQUIRE") == "1";
        if (TiberoItSessions.SkipWrite())
        {
            if (require)
            {
                throw new InvalidOperationException("교차 복사 시험은 쓰기 시험이다(ORANDA_IT_TIBERO_WRITE=1).");
            }

            return null;
        }

        if (string.IsNullOrEmpty(OracleEnv("ADMIN_PASSWORD")))
        {
            if (require)
            {
                throw new InvalidOperationException("교차 복사 시험에 Oracle 시험 DB 환경 변수가 없다: ORANDA_IT_ADMIN_PASSWORD");
            }

            return null;
        }

        var tibero = TiberoItSessions.Create();
        if (tibero is null)
        {
            return null;
        }

        OracleAccount? oracle = null;
        try
        {
            oracle = await OracleAccount.CreateAsync();
            return new CrossDbmsItEnvironment(tibero, oracle);
        }
        catch
        {
            if (oracle is not null)
            {
                await oracle.DisposeAsync();
            }

            await tibero.DisposeAsync();
            throw;
        }
    }

    // ---- 부품·서비스 ----

    /// <summary>그 DBMS 의 복사 부품 한 벌.</summary>
    public CopyDbmsParts Parts(Dbms dbms) => _parts[dbms];

    /// <summary>원본 벌·대상 벌을 직접 넣은 사전 점검(디스패처 없음).</summary>
    public CopyPrecheckService Precheck(Dbms source, Dbms target) => new(Bulk, Metadata, Policy, Parts(source), Parts(target));

    /// <summary>원본 벌·대상 벌을 직접 넣은 실행기(디스패처 없음).</summary>
    public CopyRunner Runner(Dbms source, Dbms target, ICopyRunLog log) => new(Bulk, Execution, log, Parts(source), Parts(target));

    // ---- 세션 ----

    /// <summary>시험 Oracle DB 의 주 버전(11·12·19·23·26 …). 읽지 못하면 0. <c>PRODUCT_COMPONENT_VERSION</c> 은 일반 사용자도 읽는다.</summary>
    public async Task<int> OracleMajorVersionAsync()
    {
        try
        {
            var rows = await QueryAsync(Dbms.Oracle, "SELECT VERSION FROM PRODUCT_COMPONENT_VERSION WHERE PRODUCT LIKE 'Oracle%' AND ROWNUM = 1");
            var text = rows.Count > 0 ? rows[0][0] : null;
            return text is not null && int.TryParse(text.Split('.')[0], out var major) ? major : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>복사용 세션 쌍: 원본은 읽기 전용 프로필의 전용 세션, 대상은 새 전용 세션(미커밋 작업 없음).</summary>
    public async Task<CopySessions> OpenCopySessionsAsync(Dbms source, Dbms target)
    {
        await SettleAsync();
        return new CopySessions(await OpenAsync(source, readOnly: true), await OpenAsync(target, readOnly: false));
    }

    /// <summary>전용 세션을 연다. 틀을 정리할 때 함께 닫는다.</summary>
    public async Task<ISession> OpenAsync(Dbms dbms, bool readOnly)
    {
        if (dbms == Dbms.Tibero)
        {
            return await Tibero.OpenAsync(SessionPurpose.Dedicated, Tibero.Profile() with { ReadOnly = readOnly });
        }

        var profile = new ConnectionProfile("xc10-oracle", _oracle.User, _oracle.DataSource, ReadOnly: readOnly);
        using var credential = new SecureCredential(_oracle.Password);
        return await _oracleManager.OpenAsync(profile, credential, SessionPurpose.Dedicated, CancellationToken.None);
    }

    /// <summary>그 세션의 접속 계정(복사 작업의 원본·대상 계정). 모르면 시험 실패.</summary>
    public static string Owner(ISession session)
    {
        var user = session.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(user), "the session user of a copy session is unknown");
        return user!;
    }

    /// <summary>원본 계정·대상 계정이 세션의 접속 계정인 작업(표 규칙 키 = 표 이름).</summary>
    public static CopyJob Job(CopySessions sessions, IReadOnlyList<TableRule> rules, CopyOptions? options = null, IReadOnlyList<SequenceRule>? sequences = null) => new()
    {
        Id = "xc10-it",
        Name = "xc10-it",
        Source = new ConnectionRef("xc10-source", Owner(sessions.Source)),
        Target = new ConnectionRef("xc10-target", Owner(sessions.Target)),
        SourceOwner = Owner(sessions.Source),
        TargetOwner = Owner(sessions.Target),
        Tables = rules,
        Options = options ?? new CopyOptions(),
        Sequences = sequences ?? [],
        MaskSalt = MaskFunctions.NewSalt(),
    };

    /// <summary>점검에서 고른 것 없음(트리거·FK 끄지 않음, 미리 확인한 문장 없음, 큰 시퀀스 차이 진행 안 함).</summary>
    public static CopyDecisions NoDecisions() =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(), new HashSet<string>(StringComparer.Ordinal), false);

    // ---- 표·문장 ----

    /// <summary>두 쪽에 같이 쓰는 표·시퀀스·제약 이름(<c>ORT_</c> + 접미사).</summary>
    public static string Name(string suffix) => TiberoItSessions.ObjectName(suffix);

    /// <summary>그 쪽에 표를 만든다. Tibero 는 남은 표를 먼저 지우고 끝에 다시 지운다. <paramref name="columns"/> 는 괄호 안의 열·제약 정의.</summary>
    public async Task CreateTableAsync(Dbms dbms, string table, string columns)
    {
        if (dbms == Dbms.Tibero)
        {
            await DropTiberoTableAsync(table);
            _tiberoTables.Add(table);
        }

        await ExecAsync(dbms, $"CREATE TABLE {table} ({columns})");
    }

    /// <summary>
    /// 복사가 만들 표(「없으면 만들기」 대상)가 그 쪽에 없게 한다. Tibero 는 남은 표를 지우고 끝에 다시 지운다(복사가 만든 표 정리).
    /// Oracle 은 시험마다 새 임시 사용자라 할 일이 없다.
    /// </summary>
    public async Task EnsureMissingAsync(Dbms dbms, string table)
    {
        if (dbms == Dbms.Tibero)
        {
            await DropTiberoTableAsync(table);
            _tiberoTables.Add(table);
        }
    }

    /// <summary>그 쪽에 시퀀스를 만든다(1 부터 1씩). Tibero 는 끝에 지운다.</summary>
    public async Task CreateSequenceAsync(Dbms dbms, string sequence)
    {
        if (dbms == Dbms.Tibero)
        {
            await SilentTiberoAsync($"DROP SEQUENCE {sequence}");
            _tiberoSequences.Add(sequence);
        }

        await ExecAsync(dbms, $"CREATE SEQUENCE {sequence} START WITH 1 INCREMENT BY 1");
    }

    /// <summary>
    /// 준비 문장 하나를 보내고 확정한다. Oracle = 임시 사용자로 새 연결(자동 커밋), Tibero = 준비용 세션(드라이버가 자동 커밋하지 않아 COMMIT 을 붙인다).
    /// <paramref name="oracleParameters"/>·<paramref name="tiberoBind"/> 는 값 바인드(LOB 등)가 필요할 때만.
    /// </summary>
    public async Task ExecAsync(Dbms dbms, string sql, IReadOnlyList<OracleParameter>? oracleParameters = null, Action<TiberoSession, DbCommand>? tiberoBind = null)
    {
        NoteDdl(sql);
        if (dbms == Dbms.Oracle)
        {
            await using var connection = new OracleConnection(_oracle.UserConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandText = sql;
            foreach (var p in oracleParameters ?? [])
            {
                command.Parameters.Add(p);
            }

            await command.ExecuteNonQueryAsync();
            return;
        }

        var s = await TiberoSetupAsync();
        await TiberoCommandAsync(s, sql, tiberoBind is null ? null : cmd => tiberoBind(s, cmd));
        await TiberoCommandAsync(s, "COMMIT", null);
    }

    /// <summary>Tibero 매개변수 하나(드라이버 매개변수 종류 지정). <see cref="ExecAsync"/> 의 Tibero 바인드에서 쓴다.</summary>
    public static void TiberoParam(TiberoSession s, DbCommand cmd, string name, TiberoParamKind kind, object? value)
    {
        var driver = s.Driver ?? throw new InvalidOperationException("이 시험은 실제 드라이버 세션이 필요하다.");
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        driver.SetParameterKind(p, kind);
        p.Direction = ParameterDirection.Input;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    /// <summary>앱 경로를 거치지 않는 정답 확인 조회(글자 칸). Oracle = 임시 사용자 새 연결, Tibero = 준비용 세션.</summary>
    public async Task<List<string?[]>> QueryAsync(Dbms dbms, string sql)
    {
        var rows = new List<string?[]>();
        if (dbms == Dbms.Oracle)
        {
            await using var connection = new OracleConnection(_oracle.UserConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.InitialLONGFetchSize = -1; // 사전의 LONG 열(DATA_DEFAULT·SEARCH_CONDITION)도 끝까지 읽는다
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                rows.Add(row);
            }

            return rows;
        }

        var table = await TiberoSessionQuery.RunAsync(await TiberoSetupAsync(), sql);
        rows.AddRange(table.Rows.Select(r => r.Select(TiberoSessionQuery.Str).ToArray()));
        return rows;
    }

    /// <summary>표 건수(정답 확인).</summary>
    public async Task<long> CountAsync(Dbms dbms, string table, string? where = null)
    {
        var rows = await QueryAsync(dbms, $"SELECT COUNT(*) FROM {table}" + (where is null ? string.Empty : " WHERE " + where));
        return long.Parse(rows[0][0]!, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 앱의 대량 읽기(<see cref="Bulk"/>) + 그 DBMS 코덱의 <c>ToPortable</c> 로 표를 <c>ID</c> 순으로 읽는다. 열 형식은 <paramref name="table"/>(점검 계획의 원본·대상 구조)에서 꺼낸다.
    /// 두 DBMS 의 값을 같은 이식 값으로 비교하려는 것이다(<see cref="Canon"/>).
    /// </summary>
    public async Task<List<object?[]>> ReadPortableAsync(Dbms dbms, TableInfo table, IReadOnlyList<string> columns)
    {
        var session = await ReaderAsync(dbms);
        var types = columns.Select(c => table.Columns.Single(x => string.Equals(x.Name, c, StringComparison.Ordinal)).DataType).ToArray();
        var sql = "SELECT " + string.Join(", ", columns.Select(Quote)) + " FROM " + Quote(table.Table.Owner) + "." + Quote(table.Table.Name) + " ORDER BY \"ID\"";
        var codec = Parts(dbms).Codec;
        var rows = new List<object?[]>();
        await foreach (var row in Bulk.StreamRowsAsync(session, new SqlStatement(sql, default, StatementKind.Query), NoBinds, ExecutionOptions.Default, CancellationToken.None))
        {
            rows.Add([.. row.Select((v, i) => codec.ToPortable(v, types[i]))]);
        }

        return rows;
    }

    /// <summary>마지막 DDL 뒤 <see cref="DdlSettle"/> 이 지나지 않았으면 남은 만큼 기다린다.</summary>
    public async Task SettleAsync()
    {
        if (_lastDdl != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastDdl) is var since && since < DdlSettle)
        {
            await Task.Delay(DdlSettle - since);
        }
    }

    // ---- 값 비교 ----

    /// <summary>
    /// 이식 값 하나를 비교 글자로. 시각대 값은 UTC 순간 + 오프셋(지역 이름은 빼고 비교), 긴 글자·바이트는 길이 + SHA-256.
    /// </summary>
    public static string Canon(object? portable) => portable switch
    {
        null => "<null>",
        PortableNumber n => "N:" + n.Text,
        PortableBinaryFloat f => "F:" + f.Value.ToString("R", CultureInfo.InvariantCulture),
        PortableBinaryDouble d => "D:" + d.Value.ToString("R", CultureInfo.InvariantCulture),
        string s => s.Length <= 80 ? "S:" + s : "S:len=" + N(s.Length) + " sha256=" + Hash(System.Text.Encoding.UTF8.GetBytes(s)),
        byte[] b => b.Length <= 64 ? "B:" + Convert.ToHexString(b) : "B:len=" + N(b.Length) + " sha256=" + Hash(b),
        DateTime dt => "DT:" + dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        PortableTimestamp t => "TS:" + Wall(t),
        PortableTimestampTz z => CanonTz(z, z.Offset),
        PortableIntervalYm ym => "YM:" + N(ym.Months),
        PortableIntervalDs ds => "DS:" + ds.Nanoseconds.ToString(CultureInfo.InvariantCulture),
        _ => "?" + portable.GetType().Name,
    };

    /// <summary>
    /// 원본 이식 값이 대상 열에 들어간 뒤의 기대 비교 글자(형식 지도로 정해진 변환만): BINARY_* → NUMBER 열은 그 값의 왕복 글자(D6),
    /// Tibero 로 간 길이 0 BLOB 은 NULL(D17), Tibero 로 간 시가 0 인 음수 오프셋(<c>-00:MM</c>)은 같은 순간의 <c>+00:00</c>(D7). 그 밖에는 원본 그대로.
    /// </summary>
    public static string Expect(Dbms targetDbms, ColumnInfo targetColumn, object? source)
    {
        var targetType = CrossTypeMap.Normalize(targetColumn.DataType);
        return source switch
        {
            PortableBinaryDouble d when targetType == "NUMBER" => Canon(PortableNumber.FromDouble(d.Value)),
            PortableBinaryFloat f when targetType == "NUMBER" => Canon(PortableNumber.FromSingle(f.Value)),
            byte[] { Length: 0 } when targetDbms == Dbms.Tibero && targetType == "BLOB" => Canon(null),
            PortableTimestampTz { OffsetMinutes: < 0 and > -60 } z when targetDbms == Dbms.Tibero => CanonTz(z, "+00:00"),
            _ => Canon(source),
        };
    }

    /// <summary>
    /// 원본·대상 행(같은 <c>ID</c> 순)을 열마다 비교해 다른 칸을 「ID=…/열: 기대 … ≠ 실제 …」 줄로 돌려준다. 행 수가 다르면 그 줄 하나.
    /// <paramref name="columns"/> 의 첫 열은 <c>ID</c> 여야 한다.
    /// </summary>
    public static List<string> Compare(Dbms targetDbms, TableInfo target, IReadOnlyList<string> columns, List<object?[]> sourceRows, List<object?[]> targetRows)
    {
        var mismatches = new List<string>();
        if (sourceRows.Count != targetRows.Count)
        {
            mismatches.Add("행 수: 원본 " + N(sourceRows.Count) + " ≠ 대상 " + N(targetRows.Count));
            return mismatches;
        }

        var targetColumns = columns.Select(c => target.Columns.Single(x => string.Equals(x.Name, c, StringComparison.Ordinal))).ToArray();
        for (var r = 0; r < sourceRows.Count; r++)
        {
            var id = Canon(sourceRows[r][0]);
            for (var c = 0; c < columns.Count; c++)
            {
                var expected = Expect(targetDbms, targetColumns[c], sourceRows[r][c]);
                var actual = Canon(targetRows[r][c]);
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    mismatches.Add($"{id}/{columns[c]}: 기대 {expected} ≠ 실제 {actual}");
                }
            }
        }

        return mismatches;
    }

    /// <summary>접속 정보(계정·주소·DB 이름·서비스)를 가린 글자(관찰 줄·단언 메시지용).</summary>
    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = text;
        foreach (var (secret, mask) in new[]
                 {
                     (Tibero.Env.User, "<tibero-user>"), (Tibero.Env.Host, "<tibero-host>"), (Tibero.Env.DbName, "<tibero-db>"),
                     (_oracle.User, "<oracle-user>"), (_oracle.Host, "<oracle-host>"), (_oracle.Service, "<oracle-service>"),
                 })
        {
            if (!string.IsNullOrEmpty(secret))
            {
                result = result.Replace(secret, mask, StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- 정리 ----

    public async ValueTask DisposeAsync()
    {
        // 열어 둔 Oracle 세션을 먼저 닫는다(안 그러면 DROP USER 가 접속 중인 사용자로 실패한다).
        foreach (var s in _oracleManager.Sessions.ToList())
        {
            await SilentDisposeAsync(s);
        }

        try
        {
            foreach (var table in _tiberoTables)
            {
                await DropTiberoTableAsync(table);
            }

            foreach (var sequence in _tiberoSequences)
            {
                await SilentTiberoAsync($"DROP SEQUENCE {sequence}");
            }
        }
        finally
        {
            try
            {
                await Tibero.DisposeAsync();
            }
            finally
            {
                await _oracle.DisposeAsync(); // Tibero 세션 닫기가 실패해도 Oracle 임시 사용자는 지운다
            }
        }
    }

    // ---- 내부 ----

    private static string? OracleEnv(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static T NewOracle<T>(string typeName, params object?[] args)
        where T : class
    {
        var type = OracleAssembly.GetType(typeName, throwOnError: true)!;
        return (T)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, CultureInfo.InvariantCulture)!;
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    private static string Wall(PortableTimestamp t) =>
        t.Seconds.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "." + t.Nanos.ToString("D9", CultureInfo.InvariantCulture);

    /// <summary>시각대 값 = UTC 순간(벽시계 − 오프셋) + 보인 오프셋. 오프셋을 모르면 벽시계 + 지역.</summary>
    private static string CanonTz(PortableTimestampTz z, string? shownOffset)
    {
        if (z.OffsetMinutes is not { } minutes)
        {
            return "TZ:" + Wall(z.Local) + " " + z.Region;
        }

        var utc = new PortableTimestamp(z.Local.Seconds.AddMinutes(-minutes), z.Local.Nanos);
        return "TZ:" + Wall(utc) + "Z " + shownOffset;
    }

    private void NoteDdl(string sql)
    {
        var head = sql.TrimStart();
        if (head.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) || head.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("DROP", StringComparison.OrdinalIgnoreCase))
        {
            _lastDdl = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    private async Task<TiberoSession> TiberoSetupAsync() => _tiberoSetup ??= await Tibero.OpenAsync();

    /// <summary>값 확인용 읽기 세션(Oracle = 읽기 전용 프로필, Tibero = 준비용 세션).</summary>
    private async Task<ISession> ReaderAsync(Dbms dbms)
    {
        if (dbms == Dbms.Tibero)
        {
            return await TiberoSetupAsync();
        }

        return _oracleReader ??= await OpenAsync(Dbms.Oracle, readOnly: true);
    }

    private static async Task TiberoCommandAsync(TiberoSession s, string sql, Action<DbCommand>? prepare)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = s.CreateCommand(sql);
            prepare?.Invoke(command);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    private async Task<bool> SilentTiberoAsync(string sql)
    {
        try
        {
            await TiberoCommandAsync(await TiberoSetupAsync(), sql, null);
            return true;
        }
#pragma warning disable CA1031 // 정리 단계: 없는 객체 지우기 실패는 무시한다.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task DropTiberoTableAsync(string table)
    {
        await SilentTiberoAsync("ROLLBACK");
        if (!await SilentTiberoAsync($"DROP TABLE {table} CASCADE CONSTRAINTS PURGE"))
        {
            await SilentTiberoAsync($"DROP TABLE {table} CASCADE CONSTRAINTS");
        }
    }

    private static async Task SilentDisposeAsync(ISession session)
    {
        try
        {
            await session.DisposeAsync();
        }
#pragma warning disable CA1031 // 정리 단계: 한 세션이 닫다 실패해도 나머지를 닫는다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Oracle 시험 DB 의 임시 사용자 하나(<c>ItDb</c> 방식): 관리자로 만들고(CREATE SESSION·TABLE·SEQUENCE, USERS 표 공간 무제한), 끝에 남은 세션을 끊고 <c>DROP USER … CASCADE</c>.
    /// </summary>
    private sealed class OracleAccount : IAsyncDisposable
    {
        private readonly string _adminConnectionString;

        private OracleAccount()
        {
            Host = OracleEnv("HOST") ?? "127.0.0.1";
            Service = OracleEnv("SERVICE") ?? "FREEPDB1";
            DataSource = $"{Host}:{OracleEnv("PORT") ?? "1521"}/{Service}";
            User = "ORA_XC10_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            Password = "Xc" + Guid.NewGuid().ToString("N")[..16] + "#9";
            _adminConnectionString = $"User Id={OracleEnv("ADMIN_USER") ?? "SYSTEM"};Password={OracleEnv("ADMIN_PASSWORD")};Data Source={DataSource};Pooling=false";
        }

        public string Host { get; }

        public string Service { get; }

        public string DataSource { get; }

        public string User { get; }

        public string Password { get; }

        public string UserConnectionString => $"User Id={User};Password={Password};Data Source={DataSource};Pooling=false";

        public static async Task<OracleAccount> CreateAsync()
        {
            var account = new OracleAccount();
            await account.AdminAsync($"CREATE USER {account.User} IDENTIFIED BY \"{account.Password}\" DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
            await account.AdminAsync($"GRANT CREATE SESSION, CREATE TABLE, CREATE SEQUENCE TO {account.User}");
            return account;
        }

        public async ValueTask DisposeAsync()
        {
            OracleConnection.ClearAllPools();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    await AdminAsync($"DROP USER {User} CASCADE");
                    return;
                }
                catch (OracleException)
                {
                    if (!await UserExistsAsync())
                    {
                        return;
                    }

                    await KillSessionsAsync();
                    await Task.Delay(500);
                }
            }
        }

        private async Task AdminAsync(string sql)
        {
            await using var connection = new OracleConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private async Task<bool> UserExistsAsync()
        {
            try
            {
                await using var connection = new OracleConnection(_adminConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM ALL_USERS WHERE USERNAME = :u";
                command.Parameters.Add(new OracleParameter("u", User));
                return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
            }
            catch (OracleException)
            {
                return true;
            }
        }

        private async Task KillSessionsAsync()
        {
            try
            {
                await using var connection = new OracleConnection(_adminConnectionString);
                await connection.OpenAsync();
                var sessions = new List<(long Sid, long Serial)>();
                await using (var query = connection.CreateCommand())
                {
                    query.CommandText = "SELECT SID, SERIAL# FROM V$SESSION WHERE USERNAME = :u";
                    query.Parameters.Add(new OracleParameter("u", User));
                    await using var reader = await query.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        sessions.Add((reader.GetInt64(0), reader.GetInt64(1)));
                    }
                }

                foreach (var (sid, serial) in sessions)
                {
                    try
                    {
                        await using var kill = connection.CreateCommand();
                        kill.CommandText = string.Create(CultureInfo.InvariantCulture, $"ALTER SYSTEM KILL SESSION '{sid},{serial}' IMMEDIATE");
                        await kill.ExecuteNonQueryAsync();
                    }
                    catch (OracleException)
                    {
                        // 이미 끝난 세션.
                    }
                }
            }
            catch (OracleException)
            {
                // 다음 시도에서 다시 본다.
            }
        }
    }

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}

/// <summary>교차 복사 통합 시험(<see cref="CrossDbmsItEnvironment.SkipReason"/>: Tibero 쓰기 관문 + Oracle 시험 DB).</summary>
public sealed class CrossDbmsFactAttribute : FactAttribute
{
    public CrossDbmsFactAttribute() => Skip = CrossDbmsItEnvironment.SkipReason();
}

/// <summary>교차 복사 통합 이론 시험(<see cref="CrossDbmsItEnvironment.SkipReason"/>).</summary>
public sealed class CrossDbmsTheoryAttribute : TheoryAttribute
{
    public CrossDbmsTheoryAttribute() => Skip = CrossDbmsItEnvironment.SkipReason();
}

/// <summary>실행 기록을 메모리에 남기는 가짜(실패 행·표 결과·마지막 결과). 실행마다 새 기록을 연다.</summary>
internal sealed class CrossMemoryRunLog : ICopyRunLog
{
    public List<FailedRow> FailedRows { get; } = [];

    public List<TableRunResult> Tables { get; } = [];

    public CopyOutcome? Completed { get; private set; }

    public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(new Record(this));

    public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

    public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;

    private sealed class Record(CrossMemoryRunLog log) : ICopyRunRecord
    {
        public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

        public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

        public Task RecordTableAsync(TableRunResult result, CancellationToken ct)
        {
            log.Tables.Add(result);
            return Task.CompletedTask;
        }

        public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct)
        {
            log.FailedRows.Add(row);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct)
        {
            log.Completed = outcome;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>보고를 부른 스레드에서 바로 처리하는 진행 받기(<see cref="Progress{T}"/> 는 동기화 문맥으로 미뤄 순서·시점이 흔들린다).</summary>
internal sealed class CrossSyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
