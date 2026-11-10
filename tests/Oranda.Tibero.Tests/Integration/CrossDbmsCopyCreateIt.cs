using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Tibero.Driver;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 교차 데이터 복사(Oracle↔Tibero)의 표 만들기·다시 만들기를 두 실제 서버에서 끝까지 돌리는 통합 시험(xdb-copy XC10 2단계). 대상 표를 만드는 쪽은 xc-core XC8
/// (<see cref="CrossTableMap"/> 로 옮긴 구조의 DDL, <c>CreateBlockedReason</c> 분리, 실행기의 만들기·다시 만들기 가드)이다.
/// <list type="bullet">
/// <item>틀: <see cref="CrossDbmsItEnvironment"/>(1단계 <c>CrossDbmsCopyIt</c> 와 같다). 만들 표는 Tibero 쪽이면 <c>ORT_</c> 이름이고 처음과 끝에 <c>DROP … PURGE</c> 한다
/// (<see cref="CrossDbmsItEnvironment.EnsureMissingAsync"/>).</item>
/// <item>Tibero 대상 「없으면 만들기」는 xc-core XC8b 로 열렸다(D11). Tibero 대상 다시 만들기는 TR1(D-064)에서 열렸고 그 시험은 <see cref="CrossDbmsRecreateIntoTiberoIt"/> 에 있다 — 모두 실제 부품 벌로 잰다.</item>
/// <item>순서: <c>CREATE TABLE</c> 은 두 DBMS 모두 대상의 앞 트랜잭션을 묵시 커밋한다(XC5). 실행기는 다시 만들기·없으면 만들기를 대상 쓰기 트랜잭션을 여는
/// 지우기·적재 앞에 하고, 만든 표 정리(<c>DROP … PURGE</c>)는 대상 트랜잭션을 끝낸 뒤에 한다(<c>CopyRunner.RunAsync</c>·<c>PurgeUnloadedNewTablesAsync</c>).
/// 정리 시험은 같은 실행에서 기존 표의 지우기가 「모두 끝난 뒤 커밋」으로 되돌려지는지(만들기의 묵시 커밋에 확정되지 않는지)도 함께 본다.</item>
/// <item>함수 기반·DESC 인덱스는 같은 DBMS·교차 모두 만들지 않는다(범위 밖) — 기대하지 않는다.</item>
/// <item>서버 동작을 재는 곳(문자 길이 한도·시퀀스 기본값의 사전 모양·다바이트 값의 4000바이트 한도)의 값은 <c>XC10 &lt;항목&gt;: &lt;관찰&gt;</c> 줄로 남기고, 결정(XC8d)한 동작(점검 안내·만든 열 모양·실패 행)은 단언한다.</item>
/// <item>실행(win144): <c>CrossDbmsCopyIt</c> 와 같은 환경 변수로
/// <c>dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~CrossDbmsCopyCreateIt" --logger "console;verbosity=detailed"</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class CrossDbmsCopyCreateIt(ITestOutputHelper output)
{
    private const string DateFormat = "'YYYY-MM-DD HH24:MI:SS'";
    private const string TimestampFormat = "'YYYY-MM-DD HH24:MI:SS.FF9'";

    private const string TableComment = "교차 만들기 테이블";
    private const string ColumnComment = "글자 열";

    private static readonly ResolvedPeriod Period = new(null, null);

    private static readonly string[] InvisibleColumns = ["ID", "V", "H"];

    // ---------------------------------------------------------------------------------------------------------------
    // 없으면 만들기
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 없으면 만들기(양방향): 원본 표(NUMBER(p,s)·BINARY_FLOAT/DOUBLE·VARCHAR2·CHAR·NVARCHAR2·DATE·TIMESTAMP·RAW·CLOB, Tibero 원본은 VARCHAR2(5000) 도,
    /// 이름 있는 PK·NOT NULL·DEFAULT·이름 있는 CHECK·일반 인덱스·표/열 주석)가 대상에 없을 때 복사하면 대상 표가 만들어지고 값이 옮겨진다.
    /// 열 형식은 <see cref="CrossTypeMap"/> 대로다(BINARY_* 는 Tibero 에서도 BINARY_*, Tibero VARCHAR2 4000 초과 → Oracle CLOB).
    /// Oracle→Tibero 는 실제 Tibero 방언이 만들기를 막는지 관찰 줄로 먼저 남긴다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Create_if_missing_builds_the_target_table_by_the_type_map_and_copies_the_values(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var names = CreateNames.For("XC10_CR_" + Dir(source), Dir(source));
        await CreateRichSourceAsync(env, source, names);
        await env.EnsureMissingAsync(target, names.Table);
        var rule = new TableRule(names.Table, RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true };

        var ctx = await PrecheckAsync(env, source, target, [rule]);
        Assert.DoesNotContain(ctx.Report.Items, i => i.Code == CopyCheckCodes.CreateBlocked); // XC8b: Tibero 대상 만들기가 열려 있다(D11)
        var plan = ctx.Plan(names.Table);
        Assert.True(plan.CreateMissing, "the plan does not create the missing target table");
        Assert.Contains(ctx.Report.Items, i => i.Code == CopyCheckCodes.CreateTable && i.Level == CheckLevel.Confirm);
        Assert.Contains(ctx.Report.Preview, p => p.Side == PreviewSide.Target && p.Sql.Contains("CREATE TABLE", StringComparison.Ordinal));

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, "create." + Dir(source), result);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Empty(result.FailedRows);
        Assert.Equal(3, table.TargetRows);

        var problems = await CheckRichStructureAsync(env, source, target, plan, names, "create." + Dir(source));
        var targetInfo = await TargetInfoAsync(env, ctx, target, names.Table);
        await AssertSameValuesAsync(env, source, plan, targetInfo, plan.Columns, "create." + Dir(source), problems);
    }

    /// <summary>
    /// 만든 표 정리(양방향): 기존 표 E(전체 지우고 넣기, 대상에 옛 행 5개)와 없는 표 N(없으면 만들기)을 「모두 끝난 뒤 커밋」으로 복사하다 첫 배치 뒤 중지한다.
    /// 결과는 「중지」이고, 이번에 만든 N 은 <c>DROP … PURGE</c> 로 지워져(휴지통에도 없음) 알림에 남는다. E 의 지우기는 되돌려져 옛 행 5개가 그대로다 —
    /// N 의 <c>CREATE TABLE</c>(묵시 커밋)이 E 의 DELETE 보다 먼저 가야 이렇게 된다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Created_table_is_dropped_with_purge_when_the_run_stops_and_the_earlier_delete_is_rolled_back(Dbms source)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var target = Other(source);
        var existing = CrossDbmsItEnvironment.Name("XC10_CLE_" + Dir(source));
        var created = CrossDbmsItEnvironment.Name("XC10_CLN_" + Dir(source));
        const string ddl = "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(40)";
        await env.CreateTableAsync(source, existing, ddl);
        await env.CreateTableAsync(source, created, ddl);
        await env.CreateTableAsync(target, existing, ddl);
        await env.EnsureMissingAsync(target, created);
        foreach (var t in new[] { existing, created })
        {
            await env.ExecAsync(source, $"INSERT INTO {t} (ID, V) SELECT LEVEL, 'row ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 300");
        }

        await env.ExecAsync(target, $"INSERT INTO {existing} (ID, V) SELECT 1000 + LEVEL, 'old ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 5");

        var ctx = await PrecheckAsync(env, source, target,
            [new TableRule(existing, RuleKind.All, TargetAction.DeleteAllInsert), new TableRule(created, RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true }],
            new CopyOptions { BatchSize = 10, Commit = CommitMode.AtEnd });
        Assert.True(ctx.Plan(created).CreateMissing, "the plan does not create the missing target table");
        Assert.False(ctx.Plan(existing).CreateMissing);

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
        NoteResult(env, "cleanup." + Dir(source), result);
        Note(env, "cleanup." + Dir(source) + ".recyclebin(created)", N(await env.CountAsync(target, "USER_RECYCLEBIN", $"ORIGINAL_NAME = '{created}'")) + "행");

        Assert.True(control.IsPaused, "the run never reported a loaded batch, so it was not stopped mid-load");
        Assert.Equal(CopyOutcome.Stopped, result.Outcome);
        Assert.Equal(0, await env.CountAsync(target, "USER_TABLES", $"TABLE_NAME = '{created}'"));
        Assert.Equal(0, await env.CountAsync(target, "USER_RECYCLEBIN", $"ORIGINAL_NAME = '{created}'"));
        Assert.Contains(result.Notices, n => n.Contains(created, StringComparison.Ordinal) && n.Contains("PURGE", StringComparison.Ordinal));
        Assert.Equal(5, await env.CountAsync(target, existing));
        Assert.Equal(5, await env.CountAsync(target, existing, "ID > 1000"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 다시 만들기
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 다시 만들기 Tibero→Oracle(D12 범위 안): 대상 Oracle 에 구조가 다른 옛 표(옛 열·옛 행)가 있으면 지우고 Tibero 원본 구조를 형식 지도대로 옮겨 다시 만든 뒤 넣는다.
    /// 옛 열은 없고, Tibero VARCHAR2(5000) 은 CLOB, PK·NOT NULL·DEFAULT·CHECK·인덱스·주석이 만들어지고, 값은 원본과 같다(옛 행 없음).
    /// </summary>
    [CrossDbmsFact]
    public async Task Recreate_from_tibero_into_oracle_rebuilds_the_existing_table_by_the_type_map()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        const Dbms source = Dbms.Tibero;
        const Dbms target = Dbms.Oracle;
        var names = CreateNames.For("XC10_RC_T2O", "RT2O");
        await CreateRichSourceAsync(env, source, names);
        await env.CreateTableAsync(target, names.Table, "ID NUMBER(10) PRIMARY KEY, OLD VARCHAR2(10)");
        await env.ExecAsync(target, $"INSERT INTO {names.Table} (ID, OLD) VALUES (901, 'old')");
        await env.ExecAsync(target, $"INSERT INTO {names.Table} (ID, OLD) VALUES (902, 'old')");

        var ctx = await PrecheckAsync(env, source, target, [new TableRule(names.Table, RuleKind.All, TargetAction.Recreate)]);
        Assert.Contains(ctx.Report.Items, i => i.Code == "RecreateTable" && i.Level == CheckLevel.Confirm);
        var plan = ctx.Plan(names.Table);

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, "recreate.T2O", result);
        Note(env, "recreate.T2O.recyclebin", N(await env.CountAsync(target, "USER_RECYCLEBIN", $"ORIGINAL_NAME = '{names.Table}'")) + "행(Oracle 다시 만들기는 PURGE 없이 지운다)");
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Empty(result.FailedRows);

        var problems = await CheckRichStructureAsync(env, source, target, plan, names, "recreate.T2O");
        if ((await ColumnsAsync(env, target, names.Table)).Any(c => c.Name == "OLD"))
        {
            problems.Add("옛 열 OLD 가 다시 만든 테이블에 남음");
        }

        if (await env.CountAsync(target, names.Table, "ID > 900") != 0)
        {
            problems.Add("옛 행(ID 901·902)이 남음");
        }

        var targetInfo = await TargetInfoAsync(env, ctx, target, names.Table);
        await AssertSameValuesAsync(env, source, plan, targetInfo, plan.Columns, "recreate.T2O", problems);
    }

    /// <summary>
    /// 다시 만들기 Tibero→Oracle 에서 옛 테이블을 지운 뒤 CREATE 가 실패하면(XC8e) 그 자리에서 휴지통에서 되살린다: 테이블과 옛 행이 그대로이고,
    /// 결과는 실패로 두되 「되살렸습니다」 안내가 붙는다. 사전 점검 뒤 실행 전에 다른 테이블이 같은 제약 이름을 가져가게 해 CREATE 를 실패시킨다(ORA-02264).
    /// </summary>
    [CrossDbmsFact]
    public async Task Recreate_into_oracle_whose_create_fails_after_the_drop_flashes_the_old_table_back()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        const Dbms source = Dbms.Tibero;
        const Dbms target = Dbms.Oracle;
        var table = CrossDbmsItEnvironment.Name("XC8E_FB");
        var pk = CrossDbmsItEnvironment.Name("XC8E_FBPK");
        var other = CrossDbmsItEnvironment.Name("XC8E_FBO");
        await env.CreateTableAsync(source, table, $"ID NUMBER(10), V VARCHAR2(20), CONSTRAINT {pk} PRIMARY KEY (ID)");
        await env.ExecAsync(source, $"INSERT INTO {table} (ID, V) VALUES (1, 'a')");
        await env.CreateTableAsync(target, table, "ID NUMBER(10) PRIMARY KEY, OLD VARCHAR2(10)");
        await env.ExecAsync(target, $"INSERT INTO {table} (ID, OLD) VALUES (901, 'old')");
        await env.ExecAsync(target, $"INSERT INTO {table} (ID, OLD) VALUES (902, 'old')");

        var ctx = await PrecheckAsync(env, source, target, [new TableRule(table, RuleKind.All, TargetAction.Recreate)]);
        Assert.DoesNotContain(ctx.Report.Items, i => i.Code == "RecreateBlocked");

        // 점검 뒤 실행 전에 다른 테이블이 같은 제약 이름을 가져간다: DROP 은 되고 CREATE TABLE 이 실패한다.
        await env.CreateTableAsync(target, other, $"X NUMBER(10), CONSTRAINT {pk} PRIMARY KEY (X)");

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, "recreate_flashback.T2O", result);
        var entry = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, entry.Outcome);
        Assert.Contains("다시 만들지 못했습니다", entry.Message, StringComparison.Ordinal);
        Assert.Contains("휴지통에서 되살렸습니다", entry.Message, StringComparison.Ordinal);

        Assert.Equal(1, await env.CountAsync(target, "USER_TABLES", $"TABLE_NAME = '{table}'"));
        Assert.Equal(2, await env.CountAsync(target, table, "ID > 900"));
        Assert.Equal(0, await env.CountAsync(target, table, "ID < 900"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // INVISIBLE
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Oracle INVISIBLE 열이 있는 표 → Tibero 없으면 만들기: INVISIBLE 속성만 빼고 보이는 열로 만들고(<c>SELECT *</c> 에 나옴), 점검 확인 글과 실행 결과에
    /// 「INVISIBLE 열 H 은 보이는 열로 만듭니다」 안내가 나오며, 그 열의 값도 옮겨진다.
    /// </summary>
    [CrossDbmsFact]
    public async Task Oracle_invisible_column_is_created_visible_in_tibero_with_a_notice_and_its_values_copied()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        if (await env.OracleMajorVersionAsync() is > 0 and < 12)
        {
            Note(env, "invisible", "Oracle 11g 에는 INVISIBLE 열이 없어(12c 부터) 건너뜀");
            return;
        }

        const Dbms source = Dbms.Oracle;
        const Dbms target = Dbms.Tibero;
        var t = CrossDbmsItEnvironment.Name("XC10_INV_O2T");
        await env.CreateTableAsync(source, t, "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(20), H VARCHAR2(20) INVISIBLE");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, V, H) VALUES (1, 'a', '숨은 값')");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, V, H) VALUES (2, 'b', NULL)");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, V, H) VALUES (3, NULL, 'h3')");
        await env.EnsureMissingAsync(target, t);

        var ctx = await PrecheckAsync(env, source, target, [new TableRule(t, RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true }]);
        var plan = ctx.Plan(t);
        Note(env, "invisible.plan_columns", string.Join(", ", plan.Columns));
        Assert.Contains(ctx.Report.Items, i => i.Code == CopyCheckCodes.CreateTable && i.Message.Contains("INVISIBLE 열 H", StringComparison.Ordinal));
        Assert.Contains("H", plan.Columns);

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, "invisible", result);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Contains("INVISIBLE 열 H", table.Message ?? string.Empty, StringComparison.Ordinal);

        // 보이는 열: SELECT * 가 세 열을 낸다.
        var star = await env.QueryAsync(target, $"SELECT * FROM {t} WHERE ID = 1");
        Assert.Equal(3, Assert.Single(star).Length);

        var targetInfo = await TargetInfoAsync(env, ctx, target, t);
        await AssertSameValuesAsync(env, source, plan, targetInfo, InvisibleColumns, "invisible", []);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 측정: 문자 길이 · 시퀀스 기본값 · 다바이트 값
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 측정: Oracle <c>VARCHAR2(n CHAR)</c>·<c>CHAR(n CHAR)</c> 원본을 Tibero 에 만들 때 Tibero 바이트 한도에 걸리는지 —
    /// EXTENDED <c>VARCHAR2(32767 CHAR)</c>(Oracle 이 STANDARD 면 원본을 못 만들어 건너뜀 줄), <c>CHAR(2000 CHAR)</c>, <c>VARCHAR2(4000 CHAR)</c>.
    /// 만들어졌는지(만든 문장·Tibero 사전의 길이), 한 바이트 글자·한글로 꽉 채운 값이 들어갔는지(LENGTH/LENGTHB·값 비교)를 관찰 줄로 남기고,
    /// win144 #137 에서 관찰한 결과를 단언한다: <c>VARCHAR2(4000 CHAR)</c> = 3행 모두 들어가고 값 같음, <c>CHAR(2000 CHAR)</c> = 3행 모두 들어가나
    /// Tibero 가 2000 글자로 채워 뒤 공백 수가 달라짐, <c>VARCHAR2(32767 CHAR)</c> = STANDARD Oracle 은 원본을 못 만들어(ORA-00910) 건너뜀.
    /// EXTENDED Oracle 에서 32767 CHAR 원본을 만든 경우는 아직 관찰하지 않아 관찰 줄만 남긴다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData("varchar2_32767_char")]
    [InlineData("char_2000_char")]
    [InlineData("varchar2_4000_char")]
    public async Task Oracle_char_length_columns_against_tibero_byte_limits_are_measured(string label)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        const Dbms source = Dbms.Oracle;
        const Dbms target = Dbms.Tibero;
        var (type, ascii, korean, suffix) = label switch
        {
            "varchar2_32767_char" => ("VARCHAR2(32767 CHAR)", 32767, 10922, "V32K"), // 한글 10922자 = AL32UTF8 32766바이트
            "char_2000_char" => ("CHAR(2000 CHAR)", 2000, 666, "C2K"), // 한글 666자 = 1998바이트(Oracle CHAR 한도 2000바이트)
            _ => ("VARCHAR2(4000 CHAR)", 4000, 1333, "V4K"), // 한글 1333자 = 3999바이트(STANDARD 한도 4000바이트)
        };
        var item = "char_length." + label;
        var t = CrossDbmsItEnvironment.Name("XC10_CL_" + suffix);
        Note(env, item + ".oracle_max_string_size", await TryTextAsync(env, async () =>
            Text(await env.QueryAsync(source, "SELECT VALUE FROM V$PARAMETER WHERE NAME = 'max_string_size'"))));

        var createSource = await TryTextAsync(env, async () =>
        {
            await env.CreateTableAsync(source, t, $"ID NUMBER(10) PRIMARY KEY, V {type}");
            return "OK";
        });
        Note(env, item + ".oracle_source_create", createSource);
        if (createSource != "OK")
        {
            // 원본을 못 만드는 것은 STANDARD Oracle 의 32767 CHAR 뿐이다(ORA-00910: 데이터 형식에 지정한 길이가 너무 깁니다).
            Assert.Equal("varchar2_32767_char", label);
            Assert.Contains("ORA-00910", createSource, StringComparison.Ordinal);
            Note(env, item, $"건너뜀: Oracle 원본 {type} 을 만들지 못함(EXTENDED 가 아니면 32767 CHAR 는 만들 수 없다)");
            return;
        }

        var observed = label != "varchar2_32767_char"; // 결과를 관찰해 단언하는 경우(EXTENDED 32767 CHAR 는 아직 관찰 줄만)

        foreach (var (id, value) in new[] { (1, new string('a', ascii)), (2, new string('가', korean)), (3, "짧은 값") })
        {
            Note(env, $"{item}.oracle_insert[id={N(id)}]", await TryTextAsync(env, async () =>
            {
                await env.ExecAsync(source, $"INSERT INTO {t} (ID, V) VALUES ({N(id)}, :V)", [new OracleParameter("V", OracleDbType.Varchar2) { Value = value }]);
                return "OK";
            }));
        }

        Note(env, item + ".oracle_lengths", await TryTextAsync(env, async () => Text(await env.QueryAsync(source, $"SELECT ID, LENGTH(V), LENGTHB(V) FROM {t} ORDER BY ID"))));
        await env.EnsureMissingAsync(target, t);

        var ctx = await PrecheckAsync(env, source, target, [new TableRule(t, RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true }], requireNoErrors: false, label: item);
        if (label == "char_2000_char")
        {
            // XC8d: Oracle CHAR(n CHAR) → Tibero 는 뒤 공백 수가 달라질 수 있어 통과 안내를 낸다.
            Assert.Contains(ctx.Report.Items, i => i.Table == t && i.Code == CopyCheckCodes.CharPadDiffers && i.Level == CheckLevel.Pass);
        }

        if (ctx.Report.HasErrors)
        {
            Assert.False(observed, env.Scrub(item + ": the precheck must not report errors: "
                                             + string.Join(" | ", ctx.Report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + " " + i.Message))));
            Note(env, item, "사전 점검 오류로 실행하지 않음");
            return;
        }

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, item, result);
        if (await env.CountAsync(target, "USER_TABLES", $"TABLE_NAME = '{t}'") == 0)
        {
            Assert.False(observed, item + ": the Tibero table must be created");
            Note(env, item + ".tibero_table", "만들어지지 않음(또는 정리로 지워짐)");
            return;
        }

        Note(env, item + ".tibero_column", await TryTextAsync(env, async () =>
            Text(await env.QueryAsync(target, $"SELECT DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'V'"))));
        Note(env, item + ".tibero_lengths", await TryTextAsync(env, async () => Text(await env.QueryAsync(target, $"SELECT ID, LENGTH(V), LENGTHB(V) FROM {t} ORDER BY ID"))));
        var values = await NoteValueComparisonAsync(env, ctx, target, t, item);
        if (!observed)
        {
            return;
        }

        // 세 행(한 바이트 글자로 꽉 채운 값·한글로 꽉 채운 값·짧은 값)이 모두 들어간다: Tibero 문자 단위 열은 바이트 한도에 걸리지 않는다.
        var entry = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, entry.Outcome);
        Assert.Empty(result.FailedRows);
        Assert.Equal(3, await env.CountAsync(target, t));
        if (label == "char_2000_char")
        {
            // Oracle CHAR(2000 CHAR) 는 2000바이트까지 채우고 Tibero 는 2000글자까지 채운다: 모든 행이 2000글자이고, 한글·짧은 값은 뒤 공백 수가 달라진다.
            Assert.Equal(3, await env.CountAsync(target, t, "LENGTH(V) = 2000"));
            Assert.False(values.StartsWith("값 같음", StringComparison.Ordinal), env.Scrub(item + ": trailing blanks were expected to differ: " + values));
            Assert.False(values.StartsWith("오류:", StringComparison.Ordinal), env.Scrub(item + ": " + values));
        }
        else
        {
            Assert.Equal("값 같음(행 3)", values);
        }
    }

    /// <summary>
    /// 측정 + 단언: Tibero <c>DEFAULT seq.NEXTVAL</c> 의 카탈로그 저장 모양(따옴표·스키마 포함 여부, 앱 메타데이터의 기본값)을 관찰 줄로 남기고,
    /// 그 표를 Tibero→Oracle 없으면 만들기·다시 만들기 했을 때 Oracle 표의 기본값이 원본(Tibero) 스키마를 가리키지 않는지 본다.
    /// 대상 Oracle 에 같은 이름 시퀀스(1000 부터)가 있으면 만들어지고 새 행이 그 시퀀스 값을 받아야 한다(단언). 없으면 결과만 관찰한다.
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Tibero_default_sequence_nextval_shape_and_the_oracle_default_it_becomes(bool recreate, bool targetSequence)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        const Dbms source = Dbms.Tibero;
        const Dbms target = Dbms.Oracle;
        var suffix = (recreate ? "R" : "C") + (targetSequence ? "S" : "N");
        var item = "default_seq." + suffix;
        // Oracle 11g 는 시퀀스.NEXTVAL 을 열 기본값으로 쓸 수 없다(12c 부터, ORA-00984): 대상 시퀀스가 있어도 DEFAULT 를 빼고 만들고 안내한다(XC8e).
        var legacyOracle = await env.OracleMajorVersionAsync() is > 0 and < 12;
        Note(env, item + ".oracle_major", legacyOracle ? "11 이하" : "12 이상(또는 모름)");
        var t = CrossDbmsItEnvironment.Name("XC10_DS_" + suffix);
        var sequence = CrossDbmsItEnvironment.Name("XC10_DSQ_" + suffix);
        await env.CreateSequenceAsync(source, sequence);
        await env.CreateTableAsync(source, t, $"ID NUMBER(10) DEFAULT {sequence}.NEXTVAL, V VARCHAR2(20), CONSTRAINT {CrossDbmsItEnvironment.Name("XC10_DSPK_" + suffix)} PRIMARY KEY (ID)");
        foreach (var v in new[] { "a", "b", "c" })
        {
            await env.ExecAsync(source, $"INSERT INTO {t} (V) VALUES ('{v}')");
        }

        if (targetSequence)
        {
            await env.ExecAsync(target, $"CREATE SEQUENCE {sequence} START WITH 1000 INCREMENT BY 1");
        }

        if (recreate)
        {
            await env.CreateTableAsync(target, t, "ID NUMBER(10) PRIMARY KEY, OLD VARCHAR2(5)");
        }

        var tiberoDefault = Text(await env.QueryAsync(source, $"SELECT DATA_DEFAULT FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'ID'"));
        var rule = recreate
            ? new TableRule(t, RuleKind.All, TargetAction.Recreate)
            : new TableRule(t, RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true };
        var ctx = await PrecheckAsync(env, source, target, [rule], requireNoErrors: true, label: item); // 없는 시퀀스는 오류가 아니다: 만들거나(SQ1) DEFAULT 를 빼고 만든다(XC8d)
        // SQ1(결정 76): 대상에 시퀀스가 없으면 테이블보다 먼저 원본 속성으로 만든다(대상 계정에 CREATE SEQUENCE 가 있을 때). 없으면 지금처럼 DEFAULT 를 뺀다.
        var created = ctx.Report.Items.Any(i => i.Table == t && i.Code == CopyCheckCodes.SequenceCreated);
        Note(env, item + ".sequence_created", created ? "만듦" : "만들지 않음");
        if (targetSequence)
        {
            Assert.False(created, "an existing target sequence must not be created again");
        }

        var keepsDefault = (targetSequence || created) && !legacyOracle;
        if (!keepsDefault)
        {
            var dropped = Assert.Single(ctx.Report.Items, i => i.Table == t && i.Code == CopyCheckCodes.SequenceDefaultDropped);
            Assert.Equal(CheckLevel.Confirm, dropped.Level);
        }

        var sourceOwner = CrossDbmsItEnvironment.Owner(ctx.Sessions.Source);
        Note(env, item + ".tibero_catalog_default", $"{tiberoDefault} (따옴표 {(tiberoDefault.Contains('"', StringComparison.Ordinal) ? "있음" : "없음")}, "
                                                  + $"스키마 {(tiberoDefault.Contains(sourceOwner, StringComparison.OrdinalIgnoreCase) ? "있음" : "없음")})");
        var metadataDefault = ctx.FindPlan(t)?.Source.Columns.Single(c => c.Name == "ID").DefaultValue;
        Note(env, item + ".metadata_default", metadataDefault ?? "<null 또는 계획 없음>");
        Assert.False(ctx.Report.HasErrors, "the precheck of the sequence default case must not have errors");

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, item, result);
        // 시퀀스가 대상에 있든 없든 만들기·복사는 오류 없이 끝나고 Oracle 테이블이 있어야 한다(없는 시퀀스 기본값은 XC8d 로 DEFAULT 만 빼고 만든다).
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.Equal(1, await env.CountAsync(target, "USER_TABLES", $"TABLE_NAME = '{t}'"));

        var oracleDefault = Text(await env.QueryAsync(target, $"SELECT DATA_DEFAULT FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'ID'"));
        var pointsToSource = oracleDefault.Contains(sourceOwner, StringComparison.OrdinalIgnoreCase);
        Note(env, item + ".oracle_default", $"{oracleDefault} (원본 스키마를 {(pointsToSource ? "가리킴" : "가리키지 않음")})");
        if (!keepsDefault)
        {
            // 대상에 시퀀스가 없으면 그 열의 DEFAULT 만 빼고 만들고 나머지는 그대로 복사한다.
            Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
            Assert.DoesNotContain(sequence, oracleDefault, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, await env.CountAsync(target, t));
            return;
        }

        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.False(pointsToSource, env.Scrub("the Oracle default points to the Tibero source schema: " + oracleDefault));
        Assert.Equal(3, await env.CountAsync(target, t));

        // 새 행은 대상 스키마의 시퀀스 값을 받는다: 있던 시퀀스는 1000 부터, 복사가 만든 시퀀스는 적재한 키(1~3) 뒤다.
        await env.ExecAsync(target, $"INSERT INTO {t} (V) VALUES ('new')");
        var id = (await env.QueryAsync(target, $"SELECT ID FROM {t} WHERE V = 'new'"))[0][0];
        Note(env, item + ".oracle_new_row_id", id ?? "<null>");
        Assert.True(decimal.Parse(id!, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture) >= (targetSequence ? 1000 : 4),
            "the new Oracle row did not take its ID from the target schema sequence");
    }

    /// <summary>
    /// SQ1(결정 76)의 Tibero 대상 쪽: 원본 열 <c>DEFAULT seq.NEXTVAL</c> 의 시퀀스가 대상 Tibero 스키마(첫 계정)에 없으면 「없으면 만들기」가 테이블보다 먼저
    /// 그 시퀀스를 만들고(Tibero CREATE SEQUENCE), DEFAULT 를 대상 스키마 시퀀스로 두고, 적재한 키 뒤로 시퀀스를 맞춘다.
    /// Tibero→Tibero 같은 DBMS 「없으면 만들기」가 TBR-8034(시퀀스 없음)로 실패하던 실사용 사례(dev d55f4231)의 대상 쪽이다:
    /// Tibero 는 같은 계정 복사가 막혀 있고 두 번째 계정에는 시퀀스를 만들지 않으므로(결정 59) 원본을 Oracle(12c 이상)로 둔다.
    /// 원본 쪽(Tibero 시퀀스 속성 읽기)은 <see cref="Tibero_default_sequence_nextval_shape_and_the_oracle_default_it_becomes"/> 가, 같은 DBMS 판정은 Core 단위 시험이 본다.
    /// </summary>
    [CrossDbmsFact]
    public async Task Oracle_sequence_default_into_tibero_creates_the_missing_target_sequence_before_the_table()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        const Dbms source = Dbms.Oracle;
        const Dbms target = Dbms.Tibero;
        const string item = "default_seq.O2T";
        if (await env.OracleMajorVersionAsync() is > 0 and < 12)
        {
            Note(env, item, "건너뜀: Oracle 11g 원본은 시퀀스.NEXTVAL 을 열 기본값으로 쓸 수 없다(ORA-00984)");
            return;
        }

        var t = CrossDbmsItEnvironment.Name("SQ1_O2T");
        var sequence = CrossDbmsItEnvironment.Name("SQ1_O2TQ");
        await env.EnsureMissingAsync(target, t);
        await env.EnsureSequenceMissingAsync(target, sequence); // 복사가 만든 시퀀스도 끝에 지운다
        await env.CreateSequenceAsync(source, sequence);
        await env.CreateTableAsync(source, t, $"ID NUMBER(10) DEFAULT {sequence}.NEXTVAL, V VARCHAR2(20), CONSTRAINT {CrossDbmsItEnvironment.Name("SQ1_O2TPK")} PRIMARY KEY (ID)");
        foreach (var v in new[] { "a", "b", "c" })
        {
            await env.ExecAsync(source, $"INSERT INTO {t} (V) VALUES ('{v}')");
        }

        Assert.Equal(0, await env.CountAsync(target, "USER_SEQUENCES", $"SEQUENCE_NAME = '{sequence}'"));
        var ctx = await PrecheckAsync(env, source, target, [new TableRule(t, RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true }], label: item);
        var created = ctx.Report.Items.SingleOrDefault(i => i.Table == t && i.Code == CopyCheckCodes.SequenceCreated);
        Assert.True(created is not null, env.Scrub("the missing Tibero sequence was not planned (CREATE SEQUENCE privilege of the first account?): "
                                                   + string.Join(" | ", ctx.Report.Items.Where(i => i.Table == t).Select(i => i.Code + " " + i.Message))));
        Assert.DoesNotContain(ctx.Report.Items, i => i.Table == t && i.Code == CopyCheckCodes.SequenceDefaultDropped);
        Assert.Contains(ctx.Report.Preview, p => p.Side == PreviewSide.Target && p.Sql.StartsWith("CREATE SEQUENCE", StringComparison.Ordinal)
                                                 && p.Sql.Contains(sequence, StringComparison.Ordinal));

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, item, result);
        Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, await env.CountAsync(target, "USER_SEQUENCES", $"SEQUENCE_NAME = '{sequence}'"));
        Assert.Equal(1, await env.CountAsync(target, "USER_TABLES", $"TABLE_NAME = '{t}'"));
        Assert.Equal(3, await env.CountAsync(target, t));

        var sourceOwner = CrossDbmsItEnvironment.Owner(ctx.Sessions.Source);
        var tiberoDefault = Text(await env.QueryAsync(target, $"SELECT DATA_DEFAULT FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'ID'"));
        Note(env, item + ".tibero_default", tiberoDefault);
        Assert.Contains(sequence, tiberoDefault, StringComparison.OrdinalIgnoreCase);
        Assert.False(tiberoDefault.Contains(sourceOwner, StringComparison.OrdinalIgnoreCase), env.Scrub("the Tibero default points to the Oracle source schema: " + tiberoDefault));

        // 새 행은 복사가 만든 대상 시퀀스 값을 받는다: 적재한 키(1~3) 뒤다(START WITH = 원본 LAST_NUMBER, 적재 뒤 키 최댓값 위로 맞춤).
        await env.ExecAsync(target, $"INSERT INTO {t} (V) VALUES ('new')");
        var id = (await env.QueryAsync(target, $"SELECT ID FROM {t} WHERE V = 'new'"))[0][0];
        Note(env, item + ".tibero_new_row_id", id ?? "<null>");
        Assert.True(decimal.Parse(id!, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture) >= 4,
            "the new Tibero row did not take its ID from the sequence the copy created after the loaded keys");
    }

    /// <summary>
    /// 측정: Tibero <c>VARCHAR2(n CHAR)</c>(n = 2000·4000) 의 다바이트 값(한글 1333자 = 3999바이트, 1334자 = 4002바이트, n자)을 Tibero→Oracle 없으면 만들기로 넣으면
    /// Oracle 의 4000바이트 한도(STANDARD)에서 행이 실패하는지, 만든 Oracle 열이 무엇인지(VARCHAR2(n CHAR)·CLOB), 사전 점검에 안내가 있는지를 사실만 남긴다(정책 판단 없음).
    /// </summary>
    [CrossDbmsTheory]
    [InlineData(2000)]
    [InlineData(4000)]
    public async Task Tibero_multibyte_varchar2_into_the_oracle_byte_limit_is_measured(int length)
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        const Dbms source = Dbms.Tibero;
        const Dbms target = Dbms.Oracle;
        var item = "multibyte.v" + N(length);
        var t = CrossDbmsItEnvironment.Name("XC10_MBV_" + N(length));
        await env.CreateTableAsync(source, t, $"ID NUMBER(10) PRIMARY KEY, V VARCHAR2({N(length)} CHAR)");
        var values = new[] { (1, new string('a', length)), (2, new string('가', 1333)), (3, new string('가', 1334)), (4, new string('가', length)) };
        foreach (var (id, value) in values)
        {
            await env.ExecAsync(source, $"INSERT INTO {t} (ID, V) VALUES ({N(id)}, :C1)",
                tiberoBind: (s, cmd) => CrossDbmsItEnvironment.TiberoParam(s, cmd, "C1", TiberoParamKind.Varchar2, value));
        }

        Note(env, item + ".tibero_column", Text(await env.QueryAsync(source,
            $"SELECT DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'V'")));
        Note(env, item + ".tibero_lengths", Text(await env.QueryAsync(source, $"SELECT ID, LENGTH(V), LENGTHB(V) FROM {t} ORDER BY ID")));
        Note(env, item + ".oracle_max_string_size", await TryTextAsync(env, async () =>
            Text(await env.QueryAsync(target, "SELECT VALUE FROM V$PARAMETER WHERE NAME = 'max_string_size'"))));
        // 임시 사용자는 V$PARAMETER 를 못 읽으므로 VARCHAR2(5000) 을 만들 수 있는지로 EXTENDED 인지 가린다(만들 수 있으면 4000바이트 한도가 없다).
        var extended = await TryTextAsync(env, async () =>
        {
            await env.CreateTableAsync(target, CrossDbmsItEnvironment.Name("XC10_MBP_" + N(length)), "V VARCHAR2(5000)");
            return "OK";
        }) == "OK";

        var ctx = await PrecheckAsync(env, source, target, [new TableRule(t, RuleKind.All, TargetAction.AppendOnly) { CreateIfMissing = true }], requireNoErrors: false, label: item);
        if (ctx.FindPlan(t)?.Source.Columns.Single(c => c.Name == "V") is { } column)
        {
            Note(env, item + ".metadata_source_column", $"{column.DataType} 길이 {(column.Length is { } l ? N(l) : "<null>")} → 형식 지도 "
                                                        + (CrossTypeMap.Map(source, target, column).Target?.DataType ?? "<빠짐>"));
        }

        Note(env, item + ".precheck_codes", string.Join(", ", ctx.Report.Items.Where(i => i.Table == t).Select(i => i.Code + "/" + i.Level)));
        // XC8d: 열은 그대로(VARCHAR2(n CHAR)) 두고 4000바이트를 넘을 수 있다는 확인 안내를 낸다.
        Assert.Contains(ctx.Report.Items, i => i.Table == t && i.Code == CopyCheckCodes.CrossStringBytes && i.Level == CheckLevel.Confirm);
        Assert.False(ctx.Report.HasErrors, "the multibyte VARCHAR2(n CHAR) column must not block the precheck (it is kept as is with a confirm notice)");

        var result = await RunAsync(env, ctx, new CrossMemoryRunLog());
        NoteResult(env, item, result);
        Assert.Equal(1, await env.CountAsync(target, "USER_TABLES", $"TABLE_NAME = '{t}'"));

        Note(env, item + ".oracle_column", Text(await env.QueryAsync(target,
            $"SELECT DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'V'")));
        Note(env, item + ".oracle_lengths", await TryTextAsync(env, async () => Text(await env.QueryAsync(target, $"SELECT ID, LENGTH(V) FROM {t} ORDER BY ID"))));
        var failedIds = result.FailedRows.Select(r => r.Key.GetValueOrDefault("ID") ?? "?").Order(StringComparer.Ordinal).ToArray();
        Note(env, item + ".failed_ids", string.Join(",", failedIds));
        var oracleColumn = Text(await env.QueryAsync(target,
            $"SELECT DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'V'"));
        Assert.StartsWith("VARCHAR2|", oracleColumn, StringComparison.Ordinal); // 다바이트 열도 CLOB 로 바꾸지 않는다
        Assert.EndsWith($"|{N(length)}|C", oracleColumn, StringComparison.Ordinal);
        // 한글 3바이트 계산은 Oracle DB 문자셋이 AL32UTF8 일 때만 맞는다(NLS_DATABASE_PARAMETERS 는 일반 사용자도 읽는다).
        var charset = (await env.QueryAsync(target, "SELECT VALUE FROM NLS_DATABASE_PARAMETERS WHERE PARAMETER = 'NLS_CHARACTERSET'"))[0][0];
        Note(env, item + ".oracle_charset", charset ?? "<null>");
        if (!extended && string.Equals(charset, "AL32UTF8", StringComparison.OrdinalIgnoreCase))
        {
            // STANDARD 에서는 4000바이트를 넘는 값(한글 1334자 = 4002바이트, 한글 n자 = 3n바이트)만 그 행이 실패한다.
            Assert.Equal(["3", "4"], failedIds);
            Assert.Equal(2, await env.CountAsync(target, t));
        }

        await NoteValueComparisonAsync(env, ctx, target, t, item);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 도우미
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>만들기 시험 표 하나의 이름(표·PK·CHECK·인덱스). 모두 <c>ORT_</c> 이름이다.</summary>
    private sealed record CreateNames(string Table, string PrimaryKey, string Check, string Index)
    {
        public static CreateNames For(string table, string tag) => new(
            CrossDbmsItEnvironment.Name(table), CrossDbmsItEnvironment.Name("XC10_CPK_" + tag), CrossDbmsItEnvironment.Name("XC10_CCK_" + tag),
            CrossDbmsItEnvironment.Name("XC10_CIX_" + tag));
    }

    /// <summary>대상 사전의 열 하나(<c>USER_TAB_COLUMNS</c>).</summary>
    private sealed record CatalogColumn(string Name, string DataType, string? DataLength, string? CharLength, string? CharUsed, string? NullableMark, string? Default);

    /// <summary>점검 한 번의 결과: 세션 쌍·작업·보고서와 점검·실행에 넣은 부품 벌.</summary>
    private sealed record CopyContext(CopySessions Sessions, CopyJob Job, PrecheckReport Report, CopyDbmsParts SourceParts, CopyDbmsParts TargetParts)
    {
        public CopyTablePlan Plan(string table) => Assert.Single(Report.Order, p => string.Equals(p.Rule.Table, table, StringComparison.Ordinal));

        /// <summary>점검 오류로 계획이 없을 수 있는 측정 시험용(없으면 null).</summary>
        public CopyTablePlan? FindPlan(string table) => Report.Order.FirstOrDefault(p => string.Equals(p.Rule.Table, table, StringComparison.Ordinal));
    }

    private static Dbms Other(Dbms source) => source == Dbms.Oracle ? Dbms.Tibero : Dbms.Oracle;

    private static string Dir(Dbms source) => source == Dbms.Oracle ? "O2T" : "T2O";

    private static string N(long value) => CrossDbmsItEnvironment.N(value);

    private void Note(CrossDbmsItEnvironment env, string item, string text) => output.WriteLine("XC10 " + item + ": " + env.Scrub(text));

    /// <summary>조회 결과를 한 줄 글로(행은 <c>; </c>, 칸은 <c>|</c>, NULL 은 <c>&lt;null&gt;</c>).</summary>
    private static string Text(List<string?[]> rows) =>
        rows.Count == 0 ? "(0행)" : string.Join("; ", rows.Select(r => string.Join("|", r.Select(v => v ?? "<null>"))));

    /// <summary>준비·관찰 동작 하나를 하고 그 결과 글을 돌려준다. 실패하면 <c>오류: …</c>(앞부분만).</summary>
    private static async Task<string> TryTextAsync(CrossDbmsItEnvironment env, Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
#pragma warning disable CA1031 // 측정형: 서버 오류도 관찰 결과다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ex.Message.ReplaceLineEndings(" ");
            return env.Scrub("오류: " + (message.Length > 200 ? message[..200] : message));
        }
    }

    /// <summary>
    /// 원본 벌·대상 벌을 고르고 복사 세션을 열어 사전 점검한다. 점검 항목과 대상 쪽 미리 보기 DDL 은 모두 관찰 줄로 남기고,
    /// <paramref name="requireNoErrors"/> 면 오류가 있을 때 시험 실패.
    /// </summary>
    private async Task<CopyContext> PrecheckAsync(CrossDbmsItEnvironment env, Dbms source, Dbms target, IReadOnlyList<TableRule> rules,
                                                  CopyOptions? options = null, bool requireNoErrors = true, string? label = null)
    {
        var sourceParts = env.Parts(source);
        var targetParts = env.Parts(target);
        var sessions = await env.OpenCopySessionsAsync(source, target);
        var job = CrossDbmsItEnvironment.Job(sessions, rules, options);
        var report = await new CopyPrecheckService(env.Bulk, env.Metadata, env.Policy, sourceParts, targetParts)
            .RunAsync(sessions, job, Period, null, CancellationToken.None);
        var prefix = label ?? "precheck";
        foreach (var item in report.Items)
        {
            Note(env, $"{prefix}.precheck.{item.Table ?? "*"}[{item.Code}/{item.Level}]", item.Message);
        }

        foreach (var preview in report.Preview.Where(p => p.Side == PreviewSide.Target && IsCreateDdl(p.Sql)))
        {
            Note(env, prefix + ".preview", preview.Sql);
        }

        if (requireNoErrors)
        {
            Assert.True(!report.HasErrors,
                env.Scrub("precheck errors: " + string.Join(" | ", report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + " " + i.Message))));
        }

        return new CopyContext(sessions, job, report, sourceParts, targetParts);
    }

    private static bool IsCreateDdl(string sql) =>
        sql.Contains("CREATE ", StringComparison.Ordinal) || sql.Contains("COMMENT ON", StringComparison.Ordinal) || sql.Contains("ALTER TABLE", StringComparison.Ordinal);

    private static Task<CopyRunResult> RunAsync(CrossDbmsItEnvironment env, CopyContext ctx, ICopyRunLog log, IProgress<CopyProgress>? progress = null,
                                                CopyRunControl? control = null, CancellationToken ct = default) =>
        new CopyRunner(env.Bulk, env.Execution, log, ctx.SourceParts, ctx.TargetParts)
            .RunAsync(ctx.Sessions, ctx.Job, ctx.Report, CrossDbmsItEnvironment.NoDecisions(), Period, progress, control ?? new CopyRunControl(), ct);

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

    /// <summary>
    /// 만들기 시험 원본 표: 이름 있는 PK, NOT NULL + DEFAULT 0, DEFAULT 'x', 이름 있는 CHECK, 일반 인덱스(VC), 표·열 주석. Tibero 원본은 Oracle 로 가면 CLOB 이 되는
    /// <c>VL VARCHAR2(5000)</c> 도 둔다(4500자 값). 행 3개: 값이 다 있는 행, 기본값만 받는 행(AMT·VC), 음수·큰 지수 행.
    /// </summary>
    private static async Task CreateRichSourceAsync(CrossDbmsItEnvironment env, Dbms source, CreateNames names)
    {
        var t = names.Table;
        await env.CreateTableAsync(source, t,
            $"ID NUMBER(10) CONSTRAINT {names.PrimaryKey} PRIMARY KEY, AMT NUMBER(12,3) DEFAULT 0 NOT NULL, BF BINARY_FLOAT, BD BINARY_DOUBLE, "
            + "VC VARCHAR2(100) DEFAULT 'x', CH CHAR(10), NV NVARCHAR2(50), DT DATE, TS TIMESTAMP(6), RW RAW(16), CL CLOB"
            + (source == Dbms.Tibero ? ", VL VARCHAR2(5000)" : string.Empty)
            + $", CONSTRAINT {names.Check} CHECK (AMT >= 0)");
        await env.ExecAsync(source, $"CREATE INDEX {names.Index} ON {t} (VC)");
        await env.ExecAsync(source, $"COMMENT ON TABLE {t} IS '{TableComment}'");
        await env.ExecAsync(source, $"COMMENT ON COLUMN {t}.VC IS '{ColumnComment}'");

        await env.ExecAsync(source, $"INSERT INTO {t} (ID, AMT, BF, BD, VC, CH, NV, DT, TS, RW, CL) VALUES (1, 123.456, TO_BINARY_FLOAT('0.1'), "
                                    + $"TO_BINARY_DOUBLE('15000000000'), '한글 값', 'ab', '가나다', TO_DATE('2026-11-10 13:45:59', {DateFormat}), "
                                    + $"TO_TIMESTAMP('2026-11-10 13:45:59.123456', {TimestampFormat}), HEXTORAW('00FF10AB'), 'clob 값')");
        await env.ExecAsync(source, $"INSERT INTO {t} (ID) VALUES (2)"); // AMT·VC 는 기본값을 받는다
        await env.ExecAsync(source, $"INSERT INTO {t} (ID, AMT, BF, BD, VC, CH, NV, DT, TS, RW, CL) VALUES (3, 999.999, TO_BINARY_FLOAT('-3.5'), "
                                    + $"TO_BINARY_DOUBLE('-1E+100'), 'z', 'z', 'z', TO_DATE('1999-12-31 23:59:59', {DateFormat}), "
                                    + $"TO_TIMESTAMP('2000-02-29 00:00:00.000001', {TimestampFormat}), HEXTORAW('7F'), 'z')");
        if (source == Dbms.Tibero)
        {
            var longText = new string('v', 4500);
            await env.ExecAsync(source, $"UPDATE {t} SET VL = :C1 WHERE ID = 1",
                tiberoBind: (s, cmd) => CrossDbmsItEnvironment.TiberoParam(s, cmd, "C1", TiberoParamKind.Varchar2, longText));
            await env.ExecAsync(source, $"UPDATE {t} SET VL = 'short' WHERE ID = 3");
        }
    }

    /// <summary>
    /// 만든(다시 만든) 대상 표의 구조를 사전으로 본다: 열마다 형식이 <see cref="CrossTypeMap"/> 대로인지(+ 이름으로 정한 기대: Oracle 원본의 BF·BD 는 Tibero 에서도 BINARY_*,
    /// Tibero 원본의 VL 은 Oracle CLOB), AMT NOT NULL·기본값, VC 기본값, 이름 있는 PK·CHECK, 일반 인덱스, 표·열 주석. 어긋난 것을 모아 돌려준다(값 비교 뒤 한꺼번에 단언).
    /// </summary>
    private async Task<List<string>> CheckRichStructureAsync(CrossDbmsItEnvironment env, Dbms source, Dbms target, CopyTablePlan plan, CreateNames names, string item)
    {
        var problems = new List<string>();
        var t = names.Table;
        var columns = await ColumnsAsync(env, target, t);
        foreach (var c in columns)
        {
            Note(env, item + ".column", $"{c.Name} {c.DataType} 길이 {c.DataLength ?? "-"}/{c.CharLength ?? "-"}{c.CharUsed ?? string.Empty} 널 {c.NullableMark ?? "-"} 기본 {c.Default?.Trim() ?? "<null>"}");
        }

        foreach (var sourceColumn in plan.Source.Columns)
        {
            var map = CrossTypeMap.Map(source, target, sourceColumn);
            var actual = columns.FirstOrDefault(c => c.Name == sourceColumn.Name);
            if (map.IsDropped)
            {
                if (actual is not null)
                {
                    problems.Add($"옮기지 않는 열 {sourceColumn.Name} 이 만들어짐");
                }

                continue;
            }

            var expected = CrossTypeMap.Normalize(map.Target!.DataType);
            if (actual is null)
            {
                problems.Add($"열 {sourceColumn.Name} 이 없음");
            }
            else if (CrossTypeMap.Normalize(actual.DataType) != expected)
            {
                problems.Add($"열 {sourceColumn.Name}: 형식 지도 {expected} ≠ 대상 {actual.DataType}");
            }
        }

        var named = source == Dbms.Oracle
            ? new[] { ("BF", "BINARY_FLOAT"), ("BD", "BINARY_DOUBLE") }
            : new[] { ("VL", "CLOB") };
        foreach (var (name, type) in named)
        {
            if (columns.FirstOrDefault(c => c.Name == name) is not { } column || CrossTypeMap.Normalize(column.DataType) != type)
            {
                problems.Add($"열 {name} 은 {type} 이어야 함");
            }
        }

        var amount = columns.FirstOrDefault(c => c.Name == "AMT");
        if (amount is null || amount.NullableMark != "N")
        {
            problems.Add("AMT 가 NOT NULL 이 아님");
        }

        if (amount?.Default?.Trim() != "0")
        {
            problems.Add("AMT 기본값이 0 이 아님: " + (amount?.Default ?? "<null>"));
        }

        if (columns.FirstOrDefault(c => c.Name == "VC")?.Default?.Contains("'x'", StringComparison.Ordinal) != true)
        {
            problems.Add("VC 기본값이 'x' 가 아님");
        }

        var constraints = await env.QueryAsync(target, $"SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE FROM USER_CONSTRAINTS WHERE TABLE_NAME = '{t}' ORDER BY CONSTRAINT_NAME");
        Note(env, item + ".constraints", Text(constraints));
        if (!constraints.Any(r => r[0] == names.PrimaryKey && r[1] == "P"))
        {
            problems.Add("PK " + names.PrimaryKey + " 없음");
        }

        if (!constraints.Any(r => r[0] == names.Check && r[1] == "C"))
        {
            problems.Add("CHECK " + names.Check + " 없음");
        }

        var indexes = await env.QueryAsync(target, $"SELECT INDEX_NAME FROM USER_INDEXES WHERE TABLE_NAME = '{t}' ORDER BY INDEX_NAME");
        Note(env, item + ".indexes", Text(indexes));
        if (!indexes.Any(r => r[0] == names.Index))
        {
            problems.Add("인덱스 " + names.Index + " 없음");
        }

        var tableComment = (await env.QueryAsync(target, $"SELECT COMMENTS FROM USER_TAB_COMMENTS WHERE TABLE_NAME = '{t}'")).FirstOrDefault()?[0];
        var columnComment = (await env.QueryAsync(target, $"SELECT COMMENTS FROM USER_COL_COMMENTS WHERE TABLE_NAME = '{t}' AND COLUMN_NAME = 'VC'")).FirstOrDefault()?[0];
        Note(env, item + ".comments", $"테이블 {tableComment ?? "<null>"} / VC {columnComment ?? "<null>"}");
        if (tableComment != TableComment)
        {
            problems.Add("테이블 주석이 다름");
        }

        if (columnComment != ColumnComment)
        {
            problems.Add("VC 열 주석이 다름");
        }

        return problems;
    }

    /// <summary>대상 사전의 열 목록(<c>COLUMN_ID</c> 순). Oracle 의 <c>DATA_DEFAULT</c>(LONG)는 틀이 끝까지 읽는다.</summary>
    private static async Task<List<CatalogColumn>> ColumnsAsync(CrossDbmsItEnvironment env, Dbms dbms, string table)
    {
        var rows = await env.QueryAsync(dbms,
            $"SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED, NULLABLE, DATA_DEFAULT FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{table}' ORDER BY COLUMN_ID");
        return [.. rows.Select(r => new CatalogColumn(r[0]!, r[1]!, r[2], r[3], r[4], r[5], r[6]))];
    }

    /// <summary>
    /// 만든 대상 표를 값 비교용 구조로 읽는다(사전의 열 이름·형식만). 점검 계획의 <see cref="CopyTablePlan.Target"/> 은 만들기 전 원본 구조라 쓰지 않는다
    /// (원본 형식 그대로라 대상 코덱이 바뀐 형식 — 예 CLOB — 을 읽지 못한다). 메타데이터 캐시도 거치지 않는다(다시 만들기 전 구조가 남아 있을 수 있다).
    /// </summary>
    private static async Task<TableInfo> TargetInfoAsync(CrossDbmsItEnvironment env, CopyContext ctx, Dbms target, string table)
    {
        var columns = await ColumnsAsync(env, target, table);
        return new TableInfo(new DbObjectRef(ctx.Job.TargetOwner, table, "TABLE"),
            [.. columns.Select((c, i) => new ColumnInfo(c.Name, i + 1, c.DataType))]);
    }

    /// <summary>원본(점검 계획의 원본 구조)·대상(사전 구조)을 앱 대량 읽기 + 이식 값으로 읽어 칸마다 비교하고, 앞서 모은 구조 문제와 함께 단언한다.</summary>
    private async Task AssertSameValuesAsync(CrossDbmsItEnvironment env, Dbms source, CopyTablePlan plan, TableInfo targetInfo, IReadOnlyList<string> columns,
                                             string item, List<string> problems)
    {
        var target = Other(source);
        var sourceRows = await env.ReadPortableAsync(source, plan.Source, columns);
        var targetRows = await env.ReadPortableAsync(target, targetInfo, columns);
        var mismatches = CrossDbmsItEnvironment.Compare(target, targetInfo, columns, sourceRows, targetRows);
        foreach (var line in mismatches)
        {
            Note(env, item + ".mismatch", line);
        }

        foreach (var line in problems)
        {
            Note(env, item + ".problem", line);
        }

        var all = problems.Concat(mismatches).ToList();
        Assert.True(all.Count == 0, env.Scrub(item + ": " + string.Join(" | ", all.Take(20))));
    }

    /// <summary>측정형 값 비교: 다른 칸·행 수 차이를 관찰 줄로 남기고 그 글을 돌려준다(같으면 <c>값 같음(행 n)</c>, 비교 실패면 <c>오류: …</c>). 단언은 부르는 쪽이 한다.</summary>
    private async Task<string> NoteValueComparisonAsync(CrossDbmsItEnvironment env, CopyContext ctx, Dbms target, string table, string item)
    {
        var plan = ctx.Plan(table);
        var targetInfo = await TargetInfoAsync(env, ctx, target, table);
        var text = await TryTextAsync(env, async () =>
        {
            var sourceRows = await env.ReadPortableAsync(Other(target), plan.Source, plan.Columns);
            var targetRows = await env.ReadPortableAsync(target, targetInfo, plan.Columns);
            var mismatches = CrossDbmsItEnvironment.Compare(target, targetInfo, plan.Columns, sourceRows, targetRows);
            return mismatches.Count == 0 ? "값 같음(행 " + N(targetRows.Count) + ")" : string.Join(" | ", mismatches.Take(10));
        });
        Note(env, item + ".values", text);
        return text;
    }
}
