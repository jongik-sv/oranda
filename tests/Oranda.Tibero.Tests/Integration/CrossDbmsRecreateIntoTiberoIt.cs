using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Oracle→Tibero 「테이블 다시 만들기」를 실행기 전체(<see cref="CopyPrecheckService"/> → <see cref="CopyRunner"/>)로 두 실제 서버에서 끝까지 돌리는 통합 시험(TR1, 결정 103, D-064).
/// <list type="bullet">
/// <item>틀: <see cref="CrossDbmsItEnvironment"/>(XC10 교차 복사 시험과 같다). 원본 = Oracle 시험 DB 의 임시 사용자(끝에 사용자째 지움), 대상 = 첫 번째 Tibero 계정
/// (<c>ORANDA_IT_TIBERO_*</c>, 두 번째 계정은 DROP 을 막는 트리거가 있어 쓰지 않는다). 표 이름은 <c>ORT_TR1_</c> 로 시작하고 처음과 끝에 <c>DROP … PURGE</c> 와
/// 휴지통 항목 <c>PURGE TABLE</c> 로 지운다.</item>
/// <item>다루는 위험(TB4 리뷰): 부모·자식 FK 를 함께 다시 만들 때 휴지통의 자식 FK(3), 컴파일 오류 트리거(1), 큰 바이트 길이 열(2, Oracle STANDARD 는 4000 BYTE 까지라 글자 단위로 다시 쓰는 쪽),
/// 서버가 지은 제약 이름(5), 지연 FK 안내(4). DROP 뒤 CREATE 가 실패하면 옛 표를 휴지통에서 되살리는 것도 본다(결정 66·67).</item>
/// <item>서버 동작(점검 글·알림·사전 모양)은 <c>TR1 &lt;항목&gt;: &lt;관찰&gt;</c> 줄로 남기고 결정한 동작(구조·값·결과)은 단언한다. 접속 값은 <see cref="CrossDbmsItEnvironment.Scrub"/> 으로 가린다.</item>
/// <item>실행(win144): <c>CrossDbmsCopyIt</c> 와 같은 환경 변수로
/// <c>dotnet test tests/Oranda.Tibero.Tests -c Debug -m:1 --filter "FullyQualifiedName~CrossDbmsRecreateIntoTiberoIt" --logger "console;verbosity=detailed"</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class CrossDbmsRecreateIntoTiberoIt(ITestOutputHelper output)
{
    private static readonly ResolvedPeriod Period = new(null, null);

    private static readonly string Parent = CrossDbmsItEnvironment.Name("TR1_P");
    private static readonly string Child = CrossDbmsItEnvironment.Name("TR1_C");
    private static readonly string ChildPk = CrossDbmsItEnvironment.Name("TR1_C_PK");
    private static readonly string ChildFk = CrossDbmsItEnvironment.Name("TR1_C_FK");
    private static readonly string GoodTrigger = CrossDbmsItEnvironment.Name("TR1_P_TRG");
    private static readonly string BadTrigger = CrossDbmsItEnvironment.Name("TR1_P_BAD");

    private static readonly string Flash = CrossDbmsItEnvironment.Name("TR1_FB");
    private static readonly string FlashPk = CrossDbmsItEnvironment.Name("TR1_FB_PK");
    private static readonly string FlashOther = CrossDbmsItEnvironment.Name("TR1_FBO");

    /// <summary>
    /// 부모 P(이름 없는 PK·CHECK, <c>VARCHAR2(4000 BYTE)</c>)와 자식 C(이름 있는 PK, 이름 있는 FK ON DELETE CASCADE)를 Oracle 원본으로, 구조가 다른 옛 표(옛 열·옛 행,
    /// 옛 자식의 서버 이름 FK, 켜진 정상 트리거와 지금 INVALID 인 트리거)를 Tibero 대상으로 두고 둘 다 다시 만든다.
    /// 결과는 성공이고, 구조(옛 열 없음·글자 단위 4000·서버 이름 제약은 새 서버 이름·자식 FK 는 원본 정의)·값(원본 3행씩, 4000바이트 값)·트리거(정상은 켜짐·VALID, INVALID 는 꺼짐 + 알림)가 맞다.
    /// </summary>
    [CrossDbmsFact]
    public async Task Parent_and_child_are_recreated_from_oracle_into_tibero_with_their_foreign_key_triggers_and_server_named_constraints()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        // 원본(Oracle): 이름 없는 PK·CHECK(SYS_C…), 바이트 단위 4000 열, 이름 있는 자식 PK·FK(ON DELETE CASCADE).
        await env.CreateTableAsync(Dbms.Oracle, Parent, "ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(4000 BYTE), CHECK (ID > 0)");
        await env.CreateTableAsync(Dbms.Oracle, Child,
            $"ID NUMBER(10) CONSTRAINT {ChildPk} PRIMARY KEY, PID NUMBER(10), CONSTRAINT {ChildFk} FOREIGN KEY (PID) REFERENCES {Parent} (ID) ON DELETE CASCADE");
        await env.ExecAsync(Dbms.Oracle, $"INSERT INTO {Parent} (ID, NAME) VALUES (1, RPAD('x', 4000, 'x'))");
        await env.ExecAsync(Dbms.Oracle, $"INSERT INTO {Parent} (ID, NAME) VALUES (2, 'b')");
        await env.ExecAsync(Dbms.Oracle, $"INSERT INTO {Parent} (ID, NAME) VALUES (3, NULL)");
        foreach (var (id, pid) in new[] { (1, 1), (2, 1), (3, 3) })
        {
            await env.ExecAsync(Dbms.Oracle, $"INSERT INTO {Child} (ID, PID) VALUES ({id}, {pid})");
        }

        // 대상(Tibero): 구조가 다른 옛 표. 옛 자식의 FK 는 이름 없음(서버 이름 <소유자>_CON…). 행을 넣은 뒤 트리거를 건다(INVALID 트리거가 켜져 있으면 DML 이 막힐 수 있다).
        await env.CreateTableAsync(Dbms.Tibero, Parent, "ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(10), OLD VARCHAR2(10)");
        await env.CreateTableAsync(Dbms.Tibero, Child, $"ID NUMBER(10) PRIMARY KEY, PID NUMBER(10) REFERENCES {Parent} (ID)");
        await env.ExecAsync(Dbms.Tibero, $"INSERT INTO {Parent} (ID, NAME, OLD) VALUES (901, 'old', 'old')");
        await env.ExecAsync(Dbms.Tibero, $"INSERT INTO {Parent} (ID, NAME, OLD) VALUES (902, 'old', 'old')");
        await env.ExecAsync(Dbms.Tibero, $"INSERT INTO {Child} (ID, PID) VALUES (1, 901)");
        await env.ExecAsync(Dbms.Tibero, $"CREATE TRIGGER {GoodTrigger} BEFORE INSERT ON {Parent} FOR EACH ROW\nBEGIN\n  :NEW.NAME := UPPER(:NEW.NAME);\nEND;");
        var bad = await TryTextAsync(env, async () =>
        {
            await env.ExecAsync(Dbms.Tibero, $"CREATE TRIGGER {BadTrigger} BEFORE INSERT ON {Parent} FOR EACH ROW\nBEGIN\n  :NEW.NO_SUCH_COLUMN := 1;\nEND;");
            return "accepted";
        });
        var badStatus = Text(await env.QueryAsync(Dbms.Tibero, $"SELECT status FROM user_objects WHERE object_name = '{BadTrigger}' AND object_type = 'TRIGGER'"));
        Note(env, "setup.bad_trigger", bad + "; object status = " + badStatus);
        var badInvalid = badStatus == "INVALID";

        var ctx = await PrecheckAsync(env, [new TableRule(Parent, RuleKind.All, TargetAction.Recreate), new TableRule(Child, RuleKind.All, TargetAction.Recreate)]);
        Assert.DoesNotContain(ctx.Report.Items, i => i.Code == "RecreateBlocked");
        var parentConfirm = Assert.Single(ctx.Report.Items, i => i.Table == Parent && i.Code == "RecreateTable");
        Assert.Equal(CheckLevel.Confirm, parentConfirm.Level);
        // 위험 4: 이 표를 참조하는 옛 자식의 FK(대상 카탈로그)는 지연 여부를 모른다 — 그 FK 를 이름으로 적는다.
        Assert.Contains("즉시 검사 FK 로 다시 만듭니다", parentConfirm.Message, StringComparison.Ordinal);
        Assert.Contains("." + Child + ".", parentConfirm.Message, StringComparison.Ordinal);
        if (badInvalid)
        {
            Assert.Contains(BadTrigger + "(켜짐)", parentConfirm.Message, StringComparison.Ordinal); // 위험 1: 지금 INVALID 인 트리거를 이름으로
        }

        var tiberoOwner = CrossDbmsItEnvironment.Owner(ctx.Sessions.Target).ToUpperInvariant();
        var result = await RunAsync(env, ctx);
        NoteResult(env, "recreate.O2T", result);
        foreach (var t in new[] { Parent, Child })
        {
            Note(env, "recreate.O2T.recyclebin." + t, Text(await env.QueryAsync(Dbms.Tibero, $"SELECT COUNT(*) FROM user_recyclebin WHERE original_name = '{t}'")) + "행(PURGE 없이 지운 옛 표)");
        }

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.All(result.Tables, t => Assert.Equal(CopyOutcome.Succeeded, t.Outcome));
        Assert.Empty(result.FailedRows);

        // 값: 원본 그대로 3행씩, 옛 행 없음, 4000바이트 값 그대로.
        Assert.Equal(3, await env.CountAsync(Dbms.Tibero, Parent));
        Assert.Equal(0, await env.CountAsync(Dbms.Tibero, Parent, "ID > 900"));
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, Parent, "ID = 1 AND LENGTH(NAME) = 4000"));
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, Parent, "ID = 2 AND NAME = 'b'")); // 트리거는 적재 뒤에 되살려 적재한 값을 바꾸지 않는다
        var childRows = "SELECT TO_CHAR(ID) || ':' || TO_CHAR(PID) FROM " + Child + " ORDER BY ID";
        Assert.Equal(Text(await env.QueryAsync(Dbms.Oracle, childRows)), Text(await env.QueryAsync(Dbms.Tibero, childRows)));

        // 구조: 옛 열 없음, 바이트 4000 열은 글자 단위 4000(4 × 4000 = 16000 ≤ 65532, 위험 2 의 BYTE 유지 대상이 아님).
        Assert.Equal(0, await env.CountAsync(Dbms.Tibero, "USER_TAB_COLUMNS", $"TABLE_NAME = '{Parent}' AND COLUMN_NAME = 'OLD'"));
        Note(env, "structure.NAME", Text(await env.QueryAsync(Dbms.Tibero,
            $"SELECT DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, CHAR_USED FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{Parent}' AND COLUMN_NAME = 'NAME'")));
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, "USER_TAB_COLUMNS", $"TABLE_NAME = '{Parent}' AND COLUMN_NAME = 'NAME' AND CHAR_LENGTH = 4000"));

        // 위험 5: 원본의 이름 없는 제약(SYS_C…)은 이름 없이 만들어 Tibero 가 새로 짓는다(<소유자>_CON…). Oracle 이름이 남지 않는다.
        var parentConstraints = await env.QueryAsync(Dbms.Tibero, $"SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE FROM USER_CONSTRAINTS WHERE TABLE_NAME = '{Parent}' ORDER BY CONSTRAINT_NAME");
        Note(env, "structure.parent_constraints", string.Join(", ", parentConstraints.Select(r => Prefix(r[0]!, tiberoOwner) + "/" + r[1])));
        Assert.Contains(parentConstraints, r => r[1] == "P");
        Assert.All(parentConstraints, r => Assert.True(!r[0]!.StartsWith("SYS_C", StringComparison.Ordinal) && TiberoRecreateStep.IsServerName(r[0]!, tiberoOwner),
            "a parent constraint was created with an explicit (Oracle) name instead of a Tibero server name"));
        var parentPk = parentConstraints.Single(r => r[1] == "P")[0]!;

        // 위험 3: 자식 FK 는 원본 정의(이름·ON DELETE CASCADE)로 새 부모 PK 를 참조하고 켜져 있다. 옛 자식의 서버 이름 FK 는 남지 않는다.
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, "USER_CONSTRAINTS", $"TABLE_NAME = '{Child}' AND CONSTRAINT_NAME = '{ChildPk}' AND CONSTRAINT_TYPE = 'P'"));
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, "USER_CONSTRAINTS",
            $"TABLE_NAME = '{Child}' AND CONSTRAINT_NAME = '{ChildFk}' AND CONSTRAINT_TYPE = 'R' AND STATUS = 'ENABLED' AND DELETE_RULE = 'CASCADE' AND R_CONSTRAINT_NAME = '{parentPk}'"));
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, "USER_CONSTRAINTS", $"TABLE_NAME = '{Child}' AND CONSTRAINT_TYPE = 'R'"));

        // 위험 1: 정상 트리거는 켜짐·VALID, 지금 INVALID 였던 트리거는 그대로 다시 만들어 꺼 두고 알린다(결과는 성공).
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, "USER_TRIGGERS", $"TABLE_NAME = '{Parent}' AND TRIGGER_NAME = '{GoodTrigger}' AND STATUS = 'ENABLED'"));
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, "USER_OBJECTS", $"OBJECT_NAME = '{GoodTrigger}' AND OBJECT_TYPE = 'TRIGGER' AND STATUS = 'VALID'"));
        if (badInvalid)
        {
            Assert.Equal(1, await env.CountAsync(Dbms.Tibero, "USER_TRIGGERS", $"TABLE_NAME = '{Parent}' AND TRIGGER_NAME = '{BadTrigger}' AND STATUS = 'DISABLED'"));
            Assert.Contains(result.Notices, n => n.Contains(BadTrigger + " 은 다시 만들기 전부터 컴파일 오류로 INVALID 였습니다", StringComparison.Ordinal));
        }

        // 되살린 정상 트리거가 돈다(꺼 둔 INVALID 트리거는 DML 을 막지 않는다). 확인 행은 지운다.
        await env.ExecAsync(Dbms.Tibero, $"INSERT INTO {Parent} (ID, NAME) VALUES (10, 'abc')");
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, Parent, "ID = 10 AND NAME = 'ABC'"));
        await env.ExecAsync(Dbms.Tibero, $"DELETE FROM {Parent} WHERE ID = 10");
    }

    /// <summary>
    /// Oracle→Tibero 다시 만들기에서 옛 표를 지운 뒤 CREATE 가 실패하면 그 자리에서 휴지통 이름으로 <c>FLASHBACK TABLE … TO BEFORE DROP</c> 해 표가 없어지지 않는다(결정 66·67):
    /// 표와 옛 행이 그대로이고 결과는 실패이며 「되살렸습니다」 가 붙는다. 사전 점검 뒤 실행 전에 다른 표가 같은 제약 이름을 가져가게 해 CREATE 를 실패시킨다.
    /// </summary>
    [CrossDbmsFact]
    public async Task Recreate_into_tibero_whose_create_fails_after_the_drop_flashes_the_old_table_back()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        await env.CreateTableAsync(Dbms.Oracle, Flash, $"ID NUMBER(10), V VARCHAR2(20), CONSTRAINT {FlashPk} PRIMARY KEY (ID)");
        await env.ExecAsync(Dbms.Oracle, $"INSERT INTO {Flash} (ID, V) VALUES (1, 'a')");
        await env.CreateTableAsync(Dbms.Tibero, Flash, "ID NUMBER(10) PRIMARY KEY, OLD VARCHAR2(10)");
        await env.ExecAsync(Dbms.Tibero, $"INSERT INTO {Flash} (ID, OLD) VALUES (901, 'old')");
        await env.ExecAsync(Dbms.Tibero, $"INSERT INTO {Flash} (ID, OLD) VALUES (902, 'old')");
        await env.EnsureMissingAsync(Dbms.Tibero, FlashOther);

        var ctx = await PrecheckAsync(env, [new TableRule(Flash, RuleKind.All, TargetAction.Recreate)]);
        Assert.DoesNotContain(ctx.Report.Items, i => i.Code == "RecreateBlocked");

        // 점검 뒤 실행 전에 다른 표가 같은 제약 이름을 가져간다: DROP 은 되고 CREATE TABLE 이 실패한다.
        await env.CreateTableAsync(Dbms.Tibero, FlashOther, $"X NUMBER(10), CONSTRAINT {FlashPk} PRIMARY KEY (X)");

        var result = await RunAsync(env, ctx);
        NoteResult(env, "recreate_flashback.O2T", result);
        var entry = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, entry.Outcome);
        Assert.Contains("다시 만들지 못했습니다", entry.Message, StringComparison.Ordinal);
        Assert.Contains("휴지통에서 되살렸습니다", entry.Message, StringComparison.Ordinal);

        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, "USER_TABLES", $"TABLE_NAME = '{Flash}'"));
        Assert.Equal(2, await env.CountAsync(Dbms.Tibero, Flash, "ID > 900"));
        Assert.Equal(0, await env.CountAsync(Dbms.Tibero, Flash, "ID < 900"));
        Note(env, "recreate_flashback.O2T.columns", Text(await env.QueryAsync(Dbms.Tibero, $"SELECT COLUMN_NAME FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{Flash}' ORDER BY COLUMN_ID")));
    }

    // ---- 도우미 ----

    private sealed record CopyContext(CopySessions Sessions, CopyJob Job, PrecheckReport Report, CopyDbmsParts SourceParts, CopyDbmsParts TargetParts);

    private void Note(CrossDbmsItEnvironment env, string item, string text) => output.WriteLine("TR1 " + item + ": " + env.Scrub(text));

    private static string Text(List<string?[]> rows) =>
        rows.Count == 0 ? "(0행)" : string.Join("; ", rows.Select(r => string.Join("|", r.Select(v => v ?? "<null>"))));

    /// <summary>서버 이름의 숫자 앞 부분만(접속 계정 이름은 <c>&lt;OWNER&gt;</c> 로 가린다).</summary>
    private static string Prefix(string name, string owner)
    {
        var digit = name.AsSpan().IndexOfAnyInRange('0', '9');
        var head = digit < 0 ? name : name[..digit];
        return head.Replace(owner, "<OWNER>", StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>Oracle 원본 벌·Tibero 대상 벌로 사전 점검한다. 점검 항목·대상 미리 보기는 관찰 줄로 남기고 오류가 있으면 시험 실패.</summary>
    private async Task<CopyContext> PrecheckAsync(CrossDbmsItEnvironment env, IReadOnlyList<TableRule> rules)
    {
        var sourceParts = env.Parts(Dbms.Oracle);
        var targetParts = env.Parts(Dbms.Tibero);
        var sessions = await env.OpenCopySessionsAsync(Dbms.Oracle, Dbms.Tibero);
        var job = CrossDbmsItEnvironment.Job(sessions, rules);
        var report = await new CopyPrecheckService(env.Bulk, env.Metadata, env.Policy, sourceParts, targetParts)
            .RunAsync(sessions, job, Period, null, CancellationToken.None);
        foreach (var item in report.Items)
        {
            Note(env, $"precheck.{item.Table ?? "*"}[{item.Code}/{item.Level}]", item.Message);
        }

        foreach (var preview in report.Preview.Where(p => p.Side == PreviewSide.Target))
        {
            Note(env, "preview", preview.Sql);
        }

        Assert.True(!report.HasErrors,
            env.Scrub("precheck errors: " + string.Join(" | ", report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + " " + i.Message))));
        return new CopyContext(sessions, job, report, sourceParts, targetParts);
    }

    private static Task<CopyRunResult> RunAsync(CrossDbmsItEnvironment env, CopyContext ctx) =>
        new CopyRunner(env.Bulk, env.Execution, new CrossMemoryRunLog(), ctx.SourceParts, ctx.TargetParts)
            .RunAsync(ctx.Sessions, ctx.Job, ctx.Report, CrossDbmsItEnvironment.NoDecisions(), Period, null, new CopyRunControl(), CancellationToken.None);

    private void NoteResult(CrossDbmsItEnvironment env, string item, CopyRunResult result)
    {
        Note(env, item + ".outcome", result.Outcome.ToString());
        foreach (var table in result.Tables)
        {
            Note(env, item + ".table", $"{table.Table}: {table.Outcome} 원본 {CrossDbmsItEnvironment.N(table.SourceRows)} 대상 {table.TargetRowsText} {table.Message ?? string.Empty}");
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
}
