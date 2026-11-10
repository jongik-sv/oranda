using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Driver;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 교차 데이터 복사(Oracle↔Tibero, xdb-copy XC10 1단계)를 두 실제 서버에서 끝까지 돌리는 통합 시험. 시험마다 원본 표를 만들고 행을 넣은 뒤
/// (대상 표는 미리 만들어 둔다 — 표 만들기·다시 만들기는 이 단계 범위가 아니다) 사전 점검 → 실행기로 복사하고, 대상 값을 읽어 원본과 비교한다.
/// <list type="bullet">
/// <item>틀: <see cref="CrossDbmsItEnvironment"/>(관문·Oracle 임시 사용자·Tibero <c>ORT_</c> 표·부품 직접 구성·접속 정보 가리기).</item>
/// <item>방향은 이론 시험의 인자(원본 DBMS)로 나눈다: <c>Oracle</c> = Oracle→Tibero, <c>Tibero</c> = Tibero→Oracle. 어려운 항목은 따로 시험한다(한 실패가 나머지를 가리지 않게).</item>
/// <item>값 비교는 두 DBMS 코덱의 이식 값(<see cref="CrossDbmsItEnvironment.Canon"/>)으로 하고, 형식 지도로 정해진 변환(BINARY_* → NUMBER 열, 길이 0 BLOB → NULL,
/// <c>-00:MM</c> → <c>+00:00</c>, 지역 → 오프셋)만 기대값을 바꾼다(<see cref="CrossDbmsItEnvironment.Expect"/>). 서버 동작이 미실측인 곳은 단언하지 않고
/// <c>XC10 &lt;항목&gt;: &lt;관찰&gt;</c> 줄로 남긴다.</item>
/// <item>실행(win144): <c>ORANDA_IT_TIBERO_WRITE=1</c> + Tibero(<c>ORANDA_IT_TIBERO_*</c>, <c>ORANDA_TIBERO_DRIVER</c>) + Oracle(<c>ORANDA_IT_ADMIN_PASSWORD</c> 외)
/// 환경 변수를 두고 <c>dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~CrossDbmsCopyIt" --logger "console;verbosity=detailed"</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class CrossDbmsCopyIt(ITestOutputHelper output)
{
    private const string DateFormat = "'YYYY-MM-DD HH24:MI:SS'";
    private const string TimestampFormat = "'YYYY-MM-DD HH24:MI:SS.FF9'";
    private const string TzOffsetFormat = "'YYYY-MM-DD HH24:MI:SS.FF9 TZH:TZM'";
    private const string TzRegionFormat = "'YYYY-MM-DD HH24:MI:SS.FF9 TZR'";

    private const string MatrixColumns = "ID, NPS, NBIG, BF, BD, BDN, VC, CH, NV, DT, TS, IYM, IDS, RW";

    /// <summary>형식 행렬 원본 표. <c>BDN</c> 은 원본 BINARY_DOUBLE 이고 Tibero 대상에서는 NUMBER 열이다(BINARY_* → NUMBER 값 확인).</summary>
    private const string MatrixDdl =
        "ID NUMBER(10) PRIMARY KEY, NPS NUMBER(12,3), NBIG NUMBER, BF BINARY_FLOAT, BD BINARY_DOUBLE, BDN BINARY_DOUBLE, VC VARCHAR2(100), CH CHAR(10), "
        + "NV NVARCHAR2(50), DT DATE, TS TIMESTAMP(9), IYM INTERVAL YEAR(4) TO MONTH, IDS INTERVAL DAY(5) TO SECOND(9), RW RAW(32)";

    private static readonly ResolvedPeriod Period = new(null, null);

    /// <summary>형식 행렬 행(<see cref="MatrixColumns"/> 순). 큰 수·음수·0·NULL·작은 수, 한글, 나노초, 음수 기간, RAW.</summary>
    private static readonly string[] MatrixRows =
    [
        $"1, 123456789.123, 12345678901234567890123456789012345678, TO_BINARY_FLOAT('0.1'), TO_BINARY_DOUBLE('15000000000'), TO_BINARY_DOUBLE('0.1'), 'abc', 'ab', '가나다', "
        + $"TO_DATE('2026-11-10 13:45:59', {DateFormat}), TO_TIMESTAMP('2026-11-10 13:45:59.123456789', {TimestampFormat}), TO_YMINTERVAL('12-3'), "
        + "TO_DSINTERVAL('3 04:05:06.123456789'), HEXTORAW('00FF10AB')",
        $"2, -0.001, -98765432109876543210.123456789, TO_BINARY_FLOAT('-3.5'), TO_BINARY_DOUBLE('-123.456'), TO_BINARY_DOUBLE('-1E+100'), '한글 VARCHAR2 값', '한', 'NVARCHAR2 한글', "
        + $"TO_DATE('1999-12-31 23:59:59', {DateFormat}), TO_TIMESTAMP('2000-02-29 00:00:00.000000001', {TimestampFormat}), TO_YMINTERVAL('-5-11'), "
        + "TO_DSINTERVAL('-2 23:59:59.999999999'), HEXTORAW('7F')",
        $"3, 0, 0, TO_BINARY_FLOAT('0'), TO_BINARY_DOUBLE('0'), TO_BINARY_DOUBLE('123456789.123456789'), 'x', 'x', 'x', "
        + $"TO_DATE('1970-01-01 00:00:00', {DateFormat}), TO_TIMESTAMP('1970-01-01 00:00:00.000000000', {TimestampFormat}), TO_YMINTERVAL('0-0'), "
        + "TO_DSINTERVAL('0 00:00:00'), HEXTORAW('00')",
        "4, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL",
        "5, -999999999.999, 0.000000000000000000000000000000000001, TO_BINARY_FLOAT('-0.000123'), TO_BINARY_DOUBLE('6.02214076E+23'), TO_BINARY_DOUBLE('-0.5'), "
        + "NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL",
    ];

    /// <summary>Oracle→Tibero TZ 행: 오프셋(양수·음수 분·0·<c>-00:30</c>·인도·나노 1)·지역(서울·뉴욕 여름)·NULL과 Tibero 에서 보일 오프셋.</summary>
    private static readonly TzCase[] TzCasesFromOracle =
    [
        new(1, "2026-11-10 09:00:00.123456789 +09:00", false, "+09:00"),
        new(2, "2026-11-10 09:00:00.123456789 -03:30", false, "-03:30"),
        new(3, "2026-11-10 09:00:00.123456789 +00:00", false, "+00:00"),
        new(4, "2026-11-10 09:00:00.500000000 -00:30", false, "+00:00"), // 같은 순간을 +00:00 으로(D7, Tibero 는 -00:MM 부호를 잃는다)
        new(5, "2026-11-10 09:00:00.000000001 +05:30", false, "+05:30"),
        new(6, "2026-11-10 09:00:00.123456789 Asia/Seoul", true, "+09:00"), // 지역 → 그 시각 오프셋(D7)
        new(7, "2026-07-10 09:00:00.123456789 America/New_York", true, "-04:00"), // 여름 시간
        new(8, null, false, null),
    ];

    /// <summary>Tibero→Oracle TZ 행. <c>-00:30</c> 은 Tibero 가 처음부터 틀리게 저장해(XC5) 넣지 않는다. 지역 행은 Tibero 대량 읽기가 오프셋만 줘 오프셋으로 간다.</summary>
    private static readonly TzCase[] TzCasesFromTibero =
    [
        new(1, "2026-11-10 09:00:00.123456789 +09:00", false, "+09:00"),
        new(2, "2026-11-10 09:00:00.123456789 -03:30", false, "-03:30"),
        new(3, "2026-11-10 09:00:00.123456789 +00:00", false, "+00:00"),
        new(5, "2026-11-10 09:00:00.000000001 +05:30", false, "+05:30"),
        new(6, "2026-11-10 09:00:00.123456789 Asia/Seoul", true, "+09:00"),
        new(7, "2026-07-10 09:00:00.123456789 America/New_York", true, "-04:00"),
        new(8, null, false, null),
    ];

    private static readonly string[] IdAndValue = ["ID", "V"];

    private static readonly string[] DroppedFromOracle = ["RID", "TL", "X"];

    private static readonly string[] DroppedFromTibero = ["TL"];

    private static readonly string[] NanFailedIds = ["2", "4", "6"];

    private static readonly int[] NanLoadedIds = [1, 3, 5];

    // ---------------------------------------------------------------------------------------------------------------
    // 형식 행렬
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 형식 행렬(양방향): NUMBER(p,s)·정밀 큰 수·음수·0·작은 수, BINARY_FLOAT/DOUBLE(Oracle→Tibero 는 NUMBER 바인드로 들어가 같은 값, Tibero→Oracle 은 그대로),
    /// BINARY_DOUBLE → Tibero NUMBER 열(그 값의 왕복 글자), VARCHAR2·CHAR·NVARCHAR2·한글, DATE, TIMESTAMP(9) 나노초, INTERVAL YM/DS(음수), RAW, 모든 열 NULL 행.
    /// 모든 칸이 형식 지도대로 들어갔는지 이식 값으로 비교한다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Type_matrix_values_arrive_as_the_type_map_says(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var t = CrossDbmsItEnvironment.Name("XC10_TM_" + Dir(source));
        await env.CreateTableAsync(source, t, MatrixDdl);
        await env.CreateTableAsync(target, t, target == Dbms.Tibero ? MatrixDdl.Replace("BDN BINARY_DOUBLE", "BDN NUMBER", StringComparison.Ordinal) : MatrixDdl);
        foreach (var row in MatrixRows)
        {
            await env.ExecAsync(source, $"INSERT INTO {t} ({MatrixColumns}) VALUES ({row})");
        }

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly));
        Assert.Equal(MatrixColumns.Split(", "), ctx.Plan.Columns); // 빠지는 열 없음
        if (source == Dbms.Oracle)
        {
            // BINARY_* → NUMBER 바인드는 값에 닿는 변환이라 확인 항목이다.
            Assert.Contains(ctx.Report.Items, i => i.Code == CopyCheckCodes.TypeConverted && i.Level == CheckLevel.Confirm);
        }

        var log = new CrossMemoryRunLog();
        var result = await RunAsync(env, ctx, log);
        NoteResult(env, "matrix." + Dir(source), result);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Empty(result.FailedRows);
        Assert.Equal(MatrixRows.Length, table.TargetRows);

        await AssertSameValuesAsync(env, ctx, ctx.Plan.Columns, "matrix." + Dir(source));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // TIMESTAMP WITH TIME ZONE
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// TIMESTAMP(9) WITH TIME ZONE(양방향): 넣은 순간(UTC)이 그대로이고, 대상 오프셋은 원본 오프셋이다 — 지역 이름은 그 시각의 오프셋으로,
    /// Oracle→Tibero 의 <c>-00:30</c> 은 같은 순간의 <c>+00:00</c> 으로 옮긴다(D7). 서버 표시(<c>SYS_EXTRACT_UTC</c>·<c>TZH:TZM</c>)와 이식 값 두 가지로 본다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Time_zone_values_keep_the_instant_and_regions_become_offsets(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var t = CrossDbmsItEnvironment.Name("XC10_TZ_" + Dir(source));
        const string ddl = "ID NUMBER(10) PRIMARY KEY, TZ TIMESTAMP(9) WITH TIME ZONE";
        await env.CreateTableAsync(source, t, ddl);
        await env.CreateTableAsync(target, t, ddl);
        var cases = source == Dbms.Oracle ? TzCasesFromOracle : TzCasesFromTibero;
        foreach (var c in cases)
        {
            var value = c.Text is null ? "NULL" : $"TO_TIMESTAMP_TZ('{c.Text}', {(c.Region ? TzRegionFormat : TzOffsetFormat)})";
            await env.ExecAsync(source, $"INSERT INTO {t} (ID, TZ) VALUES ({N(c.Id)}, {value})");
        }

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly));
        if (source == Dbms.Oracle)
        {
            Assert.Contains(ctx.Report.Items, i => i.Code == CopyCheckCodes.TypeConverted && i.Level == CheckLevel.Confirm);
        }

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, "tz." + Dir(source), result);
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.Empty(result.FailedRows);

        // 서버 표시: 순간(UTC)은 원본과 같고, 오프셋은 기대한 것.
        var shown = $"SELECT ID, TO_CHAR(SYS_EXTRACT_UTC(TZ), {TimestampFormat}), TO_CHAR(TZ, 'TZH:TZM'), TO_CHAR(TZ, 'TZR') FROM {t} ORDER BY ID";
        var sourceShown = await env.QueryAsync(source, shown);
        var targetShown = await env.QueryAsync(target, shown);
        Assert.Equal(cases.Length, targetShown.Count);
        var problems = new List<string>();
        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            Note(env, $"tz.{Dir(source)}.shown[id={N(c.Id)}]",
                $"원본 UTC {sourceShown[i][1] ?? "<null>"} {sourceShown[i][2] ?? "<null>"} TZR {sourceShown[i][3] ?? "<null>"} → 대상 UTC {targetShown[i][1] ?? "<null>"} {targetShown[i][2] ?? "<null>"} TZR {targetShown[i][3] ?? "<null>"}");
            if (!string.Equals(sourceShown[i][1], targetShown[i][1], StringComparison.Ordinal))
            {
                problems.Add($"id={N(c.Id)} UTC: 원본 {sourceShown[i][1] ?? "<null>"} ≠ 대상 {targetShown[i][1] ?? "<null>"}");
            }

            if (!string.Equals(c.TargetOffset, targetShown[i][2], StringComparison.Ordinal))
            {
                problems.Add($"id={N(c.Id)} 오프셋: 기대 {c.TargetOffset ?? "<null>"} ≠ 대상 {targetShown[i][2] ?? "<null>"}");
            }
        }

        Assert.True(problems.Count == 0, env.Scrub(string.Join(" | ", problems)));

        // 이식 값(앱 대량 읽기 경로)으로도 같은 순간 + 기대 오프셋.
        await AssertSameValuesAsync(env, ctx, ctx.Plan.Columns, "tz." + Dir(source));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // LOB
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 4000자 넘는 CLOB(한글 섞음)·1 MiB BLOB·짧은 CLOB·길이 0 BLOB·NULL(양방향). 길이 0 BLOB 은 Tibero 로 가면 NULL 이 된다(D17, 점검 <c>BlobEmptyToNull</c> 안내).
    /// Tibero→Oracle 에서 Tibero 원본의 길이 0 BLOB 이 어떻게 읽히는지는 관찰 줄로 남긴다(원본에서 읽은 값 그대로 Oracle 에 가는지는 단언).
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Large_clob_one_megabyte_blob_and_empty_blob_are_copied(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var t = CrossDbmsItEnvironment.Name("XC10_LOB_" + Dir(source));
        const string ddl = "ID NUMBER(10) PRIMARY KEY, CL CLOB, BL BLOB";
        await env.CreateTableAsync(source, t, ddl);
        await env.CreateTableAsync(target, t, ddl);

        var bigText = string.Concat(Enumerable.Repeat("가나다ABC123 ", 600)); // 6000자
        var oneMegabyte = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)((i * 31) % 251)).ToArray();
        var justOver = new string('x', 4001);
        await InsertLobAsync(env, source, t, 1, bigText, oneMegabyte);
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, CL, BL) VALUES (2, 'short', EMPTY_BLOB())");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, CL, BL) VALUES (3, NULL, NULL)");
        await InsertLobAsync(env, source, t, 4, justOver, [0x01, 0x02, 0x03]);

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly));
        if (target == Dbms.Tibero)
        {
            Assert.Contains(ctx.Report.Items, i => i.Code == CopyCheckCodes.BlobEmptyToNull);
        }

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, "lob." + Dir(source), result);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Empty(result.FailedRows);
        Assert.Equal(4, table.TargetRows);

        var sourceRows = await env.ReadPortableAsync(source, ctx.Plan.Source, ctx.Plan.Columns);
        var targetRows = await env.ReadPortableAsync(target, ctx.Plan.Target, ctx.Plan.Columns);
        Note(env, $"lob.{Dir(source)}.empty_blob", $"원본에서 읽은 값 {Cell(sourceRows, 1, "BL", ctx)} → 대상 {Cell(targetRows, 1, "BL", ctx)}");
        Note(env, $"lob.{Dir(source)}.big", $"원본 CL {Cell(sourceRows, 0, "CL", ctx)} BL {Cell(sourceRows, 0, "BL", ctx)} → 대상 CL {Cell(targetRows, 0, "CL", ctx)} BL {Cell(targetRows, 0, "BL", ctx)}");
        AssertNoMismatch(env, "lob." + Dir(source), CrossDbmsItEnvironment.Compare(target, ctx.Plan.Target, ctx.Plan.Columns, sourceRows, targetRows));
        Assert.True(CrossDbmsItEnvironment.Canon(targetRows[0][IndexOf(ctx, "BL")]) == CrossDbmsItEnvironment.Canon(oneMegabyte), "the 1 MiB BLOB differs from the bytes inserted");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 배치
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>행 수(157) > 배치 크기(20), 배치마다 커밋: 모든 행이 들어가고 값이 같다. 적재 진행 보고가 꽉 찬 배치 수(7) 이상 온다.</summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Rows_over_many_batches_are_all_copied(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var t = CrossDbmsItEnvironment.Name("XC10_MB_" + Dir(source));
        const string ddl = "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(40), N NUMBER(12,2)";
        await env.CreateTableAsync(source, t, ddl);
        await env.CreateTableAsync(target, t, ddl);
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, V, N) SELECT LEVEL, 'row ' || LEVEL, LEVEL * 1.25 FROM DUAL CONNECT BY LEVEL <= 157");

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly),
            new CopyOptions { BatchSize = 20, Commit = CommitMode.PerBatch });
        var loads = 0;
        var progress = new CrossSyncProgress<CopyProgress>(p =>
        {
            if (p.Log is null && p.Phase == CopyRunner.PhaseLoad && p.Rows > 0)
            {
                loads++;
            }
        });

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog(), progress);
        NoteResult(env, "batches." + Dir(source), result);
        Note(env, "batches." + Dir(source) + ".progress", "적재 진행 보고 " + N(loads) + "번");
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Equal(157, table.SourceRows);
        Assert.Equal(157, table.TargetRows);
        Assert.Equal(157, await env.CountAsync(target, t));
        Assert.True(loads >= 7, "expected a load progress report per full batch");
        await AssertSameValuesAsync(env, ctx, ctx.Plan.Columns, "batches." + Dir(source));
    }

    /// <summary>
    /// 배치 실패 → 한 행씩 → 실패 행 기록 → 실패 행 다시 넣기(양방향). 대상에만 <c>CHECK (N &lt; 1000)</c> 가 있어 ID 17(N 5000)이 든 배치가 실패하고,
    /// 한 행씩 다시 넣어 17 만 실패 행(키 = 원본 값)으로 남는다. 제약을 지운 뒤 실패 행 다시 넣기로 17 이 들어간다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Failed_batch_is_retried_row_by_row_and_the_failed_row_is_recorded_and_retried(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var t = CrossDbmsItEnvironment.Name("XC10_BF_" + Dir(source));
        var check = CrossDbmsItEnvironment.Name("XC10_BFCK_" + Dir(source));
        await env.CreateTableAsync(source, t, "ID NUMBER(10) PRIMARY KEY, N NUMBER");
        await env.CreateTableAsync(target, t, $"ID NUMBER(10) PRIMARY KEY, N NUMBER, CONSTRAINT {check} CHECK (N < 1000)");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, N) SELECT LEVEL, CASE WHEN LEVEL = 17 THEN 5000 ELSE LEVEL END FROM DUAL CONNECT BY LEVEL <= 30");

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly), new CopyOptions { BatchSize = 10 });
        var log = new CrossMemoryRunLog();
        var result = await RunAsync(env, ctx, log);
        NoteResult(env, "batch_failure." + Dir(source), result);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        var failed = Assert.Single(result.FailedRows);
        Assert.Equal("17", failed.Key["ID"]);
        Assert.Single(log.FailedRows);
        Assert.Equal(29, await env.CountAsync(target, t));
        Assert.Equal(0, await env.CountAsync(target, t, "ID = 17"));

        // 실패 행 다시 넣기: 제약을 지우면 같은 행(원본 키로 다시 읽어 같은 변환)이 들어간다.
        await env.ExecAsync(target, $"ALTER TABLE {t} DROP CONSTRAINT {check}");
        var retryLog = new CrossMemoryRunLog();
        var retry = await env.Runner(source, target, retryLog)
            .RetryFailedRowsAsync(ctx.Sessions, ctx.Job, ctx.Report, result.FailedRows, null, CancellationToken.None);
        NoteResult(env, "batch_failure.retry." + Dir(source), retry);
        Assert.Empty(retry.FailedRows);
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(retry.Tables).Outcome);
        Assert.Equal(30, await env.CountAsync(target, t));
        await AssertSameValuesAsync(env, ctx, ctx.Plan.Columns, "batch_failure." + Dir(source));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 값 변환 실패
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Oracle BINARY_DOUBLE 의 NaN·+Inf·-Inf 는 Tibero 로 NUMBER 글자 바인드할 수 없어(D6) 그 행만 실패하고(사유 = 고정 글, 값 없음), 나머지 행은 들어간다.
    /// </summary>
    [CrossDbmsFact]
    public async Task Oracle_nan_and_infinity_binary_double_fail_only_their_rows_into_tibero()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        const Dbms source = Dbms.Oracle;
        const Dbms target = Dbms.Tibero;
        var t = CrossDbmsItEnvironment.Name("XC10_NAN_O2T");
        const string ddl = "ID NUMBER(10) PRIMARY KEY, BD BINARY_DOUBLE";
        await env.CreateTableAsync(source, t, ddl);
        await env.CreateTableAsync(target, t, ddl);
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, BD) VALUES (1, 1.5d)");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, BD) VALUES (2, BINARY_DOUBLE_NAN)");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, BD) VALUES (3, 2.5d)");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, BD) VALUES (4, BINARY_DOUBLE_INFINITY)");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, BD) VALUES (5, -0.25d)");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, BD) VALUES (6, -BINARY_DOUBLE_INFINITY)");

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly));
        var log = new CrossMemoryRunLog();
        var result = await RunAsync(env, ctx, log);
        NoteResult(env, "nan.O2T", result);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.PartiallyFailed, table.Outcome);
        Assert.Equal(NanFailedIds, result.FailedRows.Select(r => r.Key["ID"]).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(result.FailedRows, r => Assert.Contains("바꾸지 못했습니다", r.Error, StringComparison.Ordinal));
        Assert.All(result.FailedRows, r =>
        {
            Assert.DoesNotContain("NaN", r.Error, StringComparison.OrdinalIgnoreCase); // 실패 사유는 고정 글이고 값은 싣지 않는다
            Assert.DoesNotContain("Infinity", r.Error, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(3, log.FailedRows.Count);
        Assert.Equal(3, table.TargetRows);

        var sourceRows = await env.ReadPortableAsync(source, ctx.Plan.Source, ctx.Plan.Columns);
        var targetRows = await env.ReadPortableAsync(target, ctx.Plan.Target, ctx.Plan.Columns);
        var loaded = sourceRows.Where(r => NanLoadedIds.Contains(int.Parse(((Oranda.Core.Transfer.Copy.CrossDbms.PortableNumber)r[0]!).Text, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        AssertNoMismatch(env, "nan.O2T", CrossDbmsItEnvironment.Compare(target, ctx.Plan.Target, ctx.Plan.Columns, loaded, targetRows));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 상대에 없는 열
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 상대 DBMS 로 옮길 형식이 없는 열(Oracle→Tibero: ROWID·TIMESTAMP WITH LOCAL TIME ZONE·XMLTYPE, Tibero→Oracle: TIMESTAMP WITH LOCAL TIME ZONE)은
    /// 점검에서 <c>ColumnDropped</c> 확인 항목으로 알리고 계획 열에서 빠지며, 실행은 그 열을 빼고 나머지 열을 복사한다(알림에 열 이름). 대상의 그 열은 NULL 로 남는다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Columns_without_a_target_type_are_dropped_with_a_notice_and_the_rest_is_copied(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var t = CrossDbmsItEnvironment.Name("XC10_DROP_" + Dir(source));
        var dropped = source == Dbms.Oracle ? DroppedFromOracle : DroppedFromTibero;
        if (source == Dbms.Oracle)
        {
            await env.CreateTableAsync(source, t, "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(20), RID ROWID, TL TIMESTAMP(6) WITH LOCAL TIME ZONE, X XMLTYPE");
            await env.CreateTableAsync(target, t, "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(20), RID VARCHAR2(40), TL VARCHAR2(40), X VARCHAR2(40)");
            await env.ExecAsync(source, $"INSERT INTO {t} (ID, V, RID, TL, X) VALUES (1, 'a', NULL, SYSTIMESTAMP, XMLTYPE('<a>1</a>'))");
            await env.ExecAsync(source, $"INSERT INTO {t} (ID, V, RID, TL, X) VALUES (2, 'b', NULL, NULL, NULL)");
        }
        else
        {
            await env.CreateTableAsync(source, t, "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(20), TL TIMESTAMP(6) WITH LOCAL TIME ZONE");
            await env.CreateTableAsync(target, t, "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(20), TL VARCHAR2(40)");
            await env.ExecAsync(source, $"INSERT INTO {t} (ID, V, TL) VALUES (1, 'a', SYSTIMESTAMP)");
            await env.ExecAsync(source, $"INSERT INTO {t} (ID, V, TL) VALUES (2, 'b', NULL)");
        }

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly));
        foreach (var column in dropped)
        {
            var item = Assert.Single(ctx.Report.Items, i => i.Code == CopyCheckCodes.ColumnDropped && i.Column == column);
            Assert.Equal(CheckLevel.Confirm, item.Level);
        }

        if (source == Dbms.Oracle)
        {
            // XMLTYPE 열 X: 위에서 ColumnDropped/Confirm 을 단언했다. 여기서는 그 열의 점검 항목 전체와 계획 열을 사실로 남긴다.
            Note(env, "dropped.O2T.xmltype", "X 항목: " + string.Join(" | ", ctx.Report.Items.Where(i => i.Column == "X").Select(i => $"{i.Code}/{i.Level}")) + " / 계획 열: " + string.Join(",", ctx.Plan.Columns));
        }

        Assert.Equal(IdAndValue, ctx.Plan.Columns);

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, "dropped." + Dir(source), result);
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.Empty(result.FailedRows);
        foreach (var column in dropped)
        {
            Assert.Contains(result.Notices, n => n.Contains(column, StringComparison.Ordinal) && n.Contains(t, StringComparison.Ordinal));
        }

        Assert.Equal(2, await env.CountAsync(target, t));
        Assert.Equal(0, await env.CountAsync(target, t, string.Join(" OR ", dropped.Select(c => c + " IS NOT NULL"))));
        await AssertSameValuesAsync(env, ctx, ctx.Plan.Columns, "dropped." + Dir(source));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 시퀀스 보정
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>대상 시퀀스(1 부터)를 표에 연결하면 복사 뒤 NEXTVAL 이 복사한 ID 최대값(125)보다 크게 옮겨진다(대상 DBMS 의 방언·카탈로그로).</summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Linked_target_sequence_is_advanced_past_the_copied_maximum(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var t = CrossDbmsItEnvironment.Name("XC10_SEQ_" + Dir(source));
        var sequence = CrossDbmsItEnvironment.Name("XC10_SQ_" + Dir(source));
        const string ddl = "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(20)";
        await env.CreateTableAsync(source, t, ddl);
        await env.CreateTableAsync(target, t, ddl);
        await env.CreateSequenceAsync(target, sequence);
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, V) SELECT 100 + LEVEL, 'v' || LEVEL FROM DUAL CONNECT BY LEVEL <= 25");

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly),
            sequences: [new SequenceRule(sequence, t, "ID")]);
        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, "sequence." + Dir(source), result);
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);

        var next = await env.QueryAsync(target, $"SELECT {sequence}.NEXTVAL FROM DUAL");
        var value = decimal.Parse(next[0][0]!, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);
        Note(env, "sequence." + Dir(source) + ".nextval", "복사 뒤 NEXTVAL " + next[0][0]);
        Assert.True(value > 125, "the target sequence was not advanced past the copied maximum ID 125");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 중지
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 중지(양방향): 첫 배치를 넣은 뒤 <see cref="CopyRunControl"/> 로 일시 정지하고 취소 토큰으로 멈춘다. 결과는 「중지」이고, 표마다 커밋이라
    /// 아직 커밋하지 않은 대상 변경은 되돌려져 대상 표가 비어 있다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Stop_while_paused_ends_as_stopped_and_rolls_back_the_target(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var t = CrossDbmsItEnvironment.Name("XC10_STOP_" + Dir(source));
        const string ddl = "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(40)";
        await env.CreateTableAsync(source, t, ddl);
        await env.CreateTableAsync(target, t, ddl);
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, V) SELECT LEVEL, 'row ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 300");

        var ctx = await PrecheckAsync(env, source, target, new TableRule(t, RuleKind.All, TargetAction.AppendOnly),
            new CopyOptions { BatchSize = 10, Commit = CommitMode.PerTable });
        using var cts = new CancellationTokenSource();
        var control = new CopyRunControl();
        var progress = new CrossSyncProgress<CopyProgress>(p =>
        {
            if (p.Log is null && p.Phase == CopyRunner.PhaseLoad && p.Rows > 0 && !control.IsPaused)
            {
                control.Pause();
                cts.Cancel();
            }
        });

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog(), progress, control, cts.Token);
        NoteResult(env, "stop." + Dir(source), result);
        Assert.True(control.IsPaused, "the run never reported a loaded batch, so it was not stopped mid-load");
        Assert.Equal(CopyOutcome.Stopped, result.Outcome);
        Assert.Equal(0, await env.CountAsync(target, t));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 도우미
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>TZ 한 행: 원본 글자(null = NULL), 지역 이름 서식 여부, 대상에서 보일 <c>TZH:TZM</c>.</summary>
    private sealed record TzCase(int Id, string? Text, bool Region, string? TargetOffset);

    /// <summary>점검 한 번의 결과: 세션 쌍·작업·보고서(계획 하나).</summary>
    private sealed record CopyContext(CopySessions Sessions, CopyJob Job, PrecheckReport Report)
    {
        public CopyTablePlan Plan => Assert.Single(Report.Order);
    }

    private static Dbms Other(Dbms source) => source == Dbms.Oracle ? Dbms.Tibero : Dbms.Oracle;

    private static string Dir(Dbms source) => source == Dbms.Oracle ? "O2T" : "T2O";

    private static string N(long value) => CrossDbmsItEnvironment.N(value);

    private void Note(CrossDbmsItEnvironment env, string item, string text) => output.WriteLine("XC10 " + item + ": " + env.Scrub(text));

    /// <summary>복사 세션을 열고 작업을 세워 사전 점검한다. 점검 항목은 모두 관찰 줄로 남기고, 오류가 있으면 시험 실패.</summary>
    private async Task<CopyContext> PrecheckAsync(CrossDbmsItEnvironment env, Dbms source, Dbms target, TableRule rule, CopyOptions? options = null,
                                                  IReadOnlyList<SequenceRule>? sequences = null)
    {
        var sessions = await env.OpenCopySessionsAsync(source, target);
        var job = CrossDbmsItEnvironment.Job(sessions, [rule], options, sequences);
        var report = await env.Precheck(source, target).RunAsync(sessions, job, Period, null, CancellationToken.None);
        foreach (var item in report.Items)
        {
            Note(env, $"precheck.{rule.Table}[{item.Code}/{item.Level}]", item.Message);
        }

        Assert.True(!report.HasErrors,
            env.Scrub("precheck errors: " + string.Join(" | ", report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + " " + i.Message))));
        return new CopyContext(sessions, job, report);
    }

    private static Task<CopyRunResult> RunAsync(CrossDbmsItEnvironment env, CopyContext ctx, ICopyRunLog log, IProgress<CopyProgress>? progress = null,
                                                CopyRunControl? control = null, CancellationToken ct = default)
    {
        var (source, target) = (ctx.Sessions.Source.Profile.Dbms, ctx.Sessions.Target.Profile.Dbms);
        return env.Runner(source, target, log).RunAsync(ctx.Sessions, ctx.Job, ctx.Report, CrossDbmsItEnvironment.NoDecisions(), Period, progress,
            control ?? new CopyRunControl(), ct);
    }

    /// <summary>실행 결과(전체·표·실패 행 키와 사유·알림)를 관찰 줄로 남긴다.</summary>
    private void NoteResult(CrossDbmsItEnvironment env, string item, CopyRunResult result)
    {
        Note(env, item + ".outcome", result.Outcome.ToString());
        foreach (var table in result.Tables)
        {
            Note(env, item + ".table", $"{table.Table}: {table.Outcome} 원본 {N(table.SourceRows)} 대상 {table.TargetRowsText} {table.Message ?? string.Empty}");
        }

        foreach (var row in result.FailedRows)
        {
            Note(env, item + ".failed_row", string.Join(",", row.Key.Select(k => k.Key + "=" + (k.Value ?? "<null>"))) + " " + row.Error);
        }

        foreach (var notice in result.Notices)
        {
            Note(env, item + ".notice", notice);
        }
    }

    /// <summary>원본·대상을 앱 대량 읽기 + 이식 값으로 읽어 형식 지도의 기대값과 칸마다 비교한다.</summary>
    private async Task AssertSameValuesAsync(CrossDbmsItEnvironment env, CopyContext ctx, IReadOnlyList<string> columns, string item)
    {
        var source = ctx.Sessions.Source.Profile.Dbms;
        var target = ctx.Sessions.Target.Profile.Dbms;
        var sourceRows = await env.ReadPortableAsync(source, ctx.Plan.Source, columns);
        var targetRows = await env.ReadPortableAsync(target, ctx.Plan.Target, columns);
        AssertNoMismatch(env, item, CrossDbmsItEnvironment.Compare(target, ctx.Plan.Target, columns, sourceRows, targetRows));
    }

    private void AssertNoMismatch(CrossDbmsItEnvironment env, string item, List<string> mismatches)
    {
        foreach (var line in mismatches)
        {
            Note(env, item + ".mismatch", line);
        }

        Assert.True(mismatches.Count == 0, env.Scrub(item + ": " + string.Join(" | ", mismatches.Take(20))));
    }

    private static int IndexOf(CopyContext ctx, string column) => ctx.Plan.Columns.ToList().IndexOf(column);

    private static string Cell(List<object?[]> rows, int row, string column, CopyContext ctx) =>
        row < rows.Count ? CrossDbmsItEnvironment.Canon(rows[row][IndexOf(ctx, column)]) : "(행 없음)";

    /// <summary>LOB 한 행을 바인드로 넣는다(Oracle = ODP.NET CLOB·BLOB 매개변수, Tibero = 드라이버 Clob·Blob 종류).</summary>
    private static Task InsertLobAsync(CrossDbmsItEnvironment env, Dbms dbms, string table, int id, string clob, byte[] blob)
    {
        var sql = $"INSERT INTO {table} (ID, CL, BL) VALUES ({N(id)}, :C1, :C2)";
        if (dbms == Dbms.Oracle)
        {
            return env.ExecAsync(dbms, sql,
                [new OracleParameter("C1", OracleDbType.Clob) { Value = clob }, new OracleParameter("C2", OracleDbType.Blob) { Value = blob }]);
        }

        return env.ExecAsync(dbms, sql, tiberoBind: (s, cmd) =>
        {
            CrossDbmsItEnvironment.TiberoParam(s, cmd, "C1", TiberoParamKind.Clob, clob);
            CrossDbmsItEnvironment.TiberoParam(s, cmd, "C2", TiberoParamKind.Blob, blob);
        });
    }
}
