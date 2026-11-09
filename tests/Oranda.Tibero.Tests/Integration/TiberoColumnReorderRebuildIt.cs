using System.Runtime.ExceptionServices;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Language.Lexing;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero 열 순서 바꾸기 다시 만들기(CTAS + RENAME + 종속 객체 다시 붙이기)를 실제 서버로 끝까지 돌려 본다(D4b).
/// 이 시험이 통과하면 <see cref="TiberoColumnReorderScripts.DependentDdlReady"/> 를 true 로 둘 수 있다.
/// 쓰기 시험(<c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만, ORT_ 이름만). 앱과 같은 길로 스크립트를 만든다:
/// <c>GetTableAsync</c> → <c>GetDdlAsync</c> 둘(표만·종속 포함) → <c>DependentPart</c> → <c>Rebuild</c> → 편집기와 같은 분리기로 나눠 문장마다 실행.
/// <list type="bullet">
/// <item>시험 1: 기본키·유일 키·검사·자기 참조 외래키·기본값·인덱스·주석·PUBLIC 권한·행 트리거가 달린 표를 거꾸로 바꾼다.</item>
/// <item>시험 2: 가상 열이 있는 표. 만들기·다시 붙이기(<c>ADD (… AS (…))</c>)가 서버에서 받아지는지 따로 본다.</item>
/// </list>
/// 접속 값(계정·DB·호스트)은 단언 메시지·출력에 넣지 않는다: 참·거짓 단언만 쓰고, 예외는 형식 이름·오류 번호만 남긴다.
/// 시험마다 시작할 때 남은 제 표를 지우고, 끝나면 표·<c>$NEW</c>·<c>$OLD</c> 만 지운다(정리 실패가 본문 실패를 가리지 않는다).
/// </summary>
[Trait("Category", "Integration")]
[Collection("TiberoPublicGrant")] // #117 TBR-12033
public class TiberoColumnReorderRebuildIt(ITestOutputHelper output)
{
    private static readonly string MainTable = TiberoItSessions.ObjectName("REORDER_RB");
    private static readonly string Pk = TiberoItSessions.ObjectName("RB_PK");
    private static readonly string Uk = TiberoItSessions.ObjectName("RB_UK");
    private static readonly string Ck = TiberoItSessions.ObjectName("RB_CK");
    private static readonly string Fk = TiberoItSessions.ObjectName("RB_FK");
    private static readonly string Index = TiberoItSessions.ObjectName("RB_IX");
    private static readonly string Trigger = TiberoItSessions.ObjectName("RB_TRG");
    private static readonly string VirtualTable = TiberoItSessions.ObjectName("REORDER_RBV");

    private static readonly ColumnReorderTraits PlainTable = new(false, false, null);

    private static readonly string[] ExpectedRows = ["1|a|c1|5|-", "2|b|c2|0|1", "3|c|c3|7|1"];

    [TiberoWriteFact]
    public Task Rebuild_keeps_data_constraints_index_trigger_comments_and_grants() => WriteAsync(MainTable, RebuildMainAsync);

    /// <summary>
    /// D4c: 휴지통이 켜진 세션. 스크립트의 <c>DROP TABLE "T$OLD"</c> 는 PURGE 가 없어 원본이 휴지통으로 간다 — 그때도 원본의 인덱스·제약 이름이 비어
    /// 종속 DDL(같은 이름)이 충돌 없이 다시 붙는지 본다(서버 기본은 휴지통 꺼짐이라 시험 1 은 이 경우를 덮지 않는다).
    /// </summary>
    [TiberoWriteFact]
    public Task Rebuild_with_the_recycle_bin_on_reattaches_dependents_without_name_clashes() => WriteAsync(MainTable, async (s, o) =>
    {
        string[] own = [MainTable + "$OLD", MainTable + "$NEW", MainTable];
        await PurgeOwnBinItemsAsync(s, own); // 지난 실행의 남은 항목이 아래 단언을 거짓으로 채우지 않게
        await ExecQuietAsync(s, "recycle bin on", "ALTER SESSION SET USE_RECYCLEBIN = Y");
        try
        {
            await RebuildMainAsync(s, o);
            var binned = await CountAsync(s, $"SELECT COUNT(*) FROM user_recyclebin WHERE original_name = '{MainTable}$OLD'");
            Assert.True(binned == 1, "the dropped $OLD table is not in the recycle bin exactly once (recycle bin was not on?)");
        }
        finally
        {
            // 이 시험이 지운 표의 휴지통 항목만(PURGE RECYCLEBIN 은 계정 휴지통의 다른 항목까지 지운다).
            await PurgeOwnBinItemsAsync(s, own);
            await TryExecAsync(s, "ALTER SESSION SET USE_RECYCLEBIN = N");
        }
    });

    [TiberoWriteFact]
    public Task Rebuild_re_adds_a_virtual_column_after_the_copied_columns() => WriteAsync(VirtualTable, (s, o) => RebuildVirtualAsync(s, o));

    private static async Task RebuildMainAsync(TiberoSession session, string owner)
    {
        var t = MainTable;
        string[] setup =
        [
            $"CREATE TABLE {t} (ID NUMBER NOT NULL, NAME VARCHAR2(30) DEFAULT 'x', CODE VARCHAR2(10), QTY NUMBER(5) DEFAULT 0, PARENT_ID NUMBER, " +
            $"CONSTRAINT {Pk} PRIMARY KEY (ID), CONSTRAINT {Uk} UNIQUE (CODE), CONSTRAINT {Ck} CHECK (QTY >= 0), " +
            $"CONSTRAINT {Fk} FOREIGN KEY (PARENT_ID) REFERENCES {t} (ID))",
            $"CREATE INDEX {Index} ON {t} (NAME)",
            $"COMMENT ON TABLE {t} IS 'rb table'",
            $"COMMENT ON COLUMN {t}.NAME IS 'rb name'",
            $"GRANT SELECT ON {t} TO PUBLIC",
            $"CREATE TRIGGER {Trigger} BEFORE INSERT ON {t} FOR EACH ROW\nBEGIN\n  NULL;\nEND;",
            $"INSERT INTO {t} (ID, NAME, CODE, QTY, PARENT_ID) VALUES (1, 'a', 'c1', 5, NULL)",
            $"INSERT INTO {t} (ID, NAME, CODE, QTY, PARENT_ID) VALUES (2, 'b', 'c2', 0, 1)",
            $"INSERT INTO {t} (ID, NAME, CODE, QTY, PARENT_ID) VALUES (3, 'c', 'c3', 7, 1)",
            "COMMIT",
        ];
        for (var i = 0; i < setup.Length; i++)
        {
            await ExecQuietAsync(session, "setup statement " + i, setup[i]);
        }

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {t}") == 3, "setup: 3 rows expected");

        var (script, original) = await BuildScriptAsync(session, owner, t, c => [.. Enumerable.Reverse(c)]);
        await RunScriptAsync(session, script);

        var desired = Enumerable.Reverse(original).ToList();
        var after = await ColumnsAsync(session, t);
        Assert.True(after.SequenceEqual(desired, StringComparer.Ordinal), "column_id order is not the desired (reversed) order: " + string.Join(",", after));

        var rows = await RowsAsync(session,
            $"SELECT ID || '|' || NAME || '|' || CODE || '|' || QTY || '|' || NVL(TO_CHAR(PARENT_ID), '-') FROM {t} ORDER BY ID");
        Assert.True(rows.SequenceEqual(ExpectedRows, StringComparer.Ordinal), "rows changed by the rebuild: " + string.Join(" ; ", rows));

        // 기본값: 카탈로그에 남았는가, 실제로 채워지는가.
        var info = await QuietAsync("GetTableAsync(after)", () => new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync)
            .GetTableAsync(session, new DbObjectRef(owner, t, "TABLE"), CancellationToken.None));
        var nameDefault = info.Columns.FirstOrDefault(c => c.Name == "NAME")?.DefaultValue;
        var qtyDefault = info.Columns.FirstOrDefault(c => c.Name == "QTY")?.DefaultValue;
        Assert.True(nameDefault is not null && nameDefault.Contains('x', StringComparison.Ordinal), "NAME default is missing after the rebuild");
        Assert.True(qtyDefault is not null && qtyDefault.Contains('0', StringComparison.Ordinal), "QTY default is missing after the rebuild");
        Assert.True(info.Columns.FirstOrDefault(c => c.Name == "ID")?.IsNullable == false, "ID NOT NULL is lost after the rebuild");
        await ExecQuietAsync(session, "insert using defaults", $"INSERT INTO {t} (ID) VALUES (4)");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {t} WHERE ID = 4 AND NAME = 'x' AND QTY = 0") == 1, "defaults are not applied to a new row");
        await ExecQuietAsync(session, "rollback test row", "ROLLBACK");

        // 제약: 이름 있는 것은 이름으로, 모두 ENABLED.
        foreach (var (type, name) in new[] { ("P", Pk), ("U", Uk), ("C", Ck), ("R", Fk) })
        {
            Assert.True(
                await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{t}' AND constraint_name = '{name}' AND constraint_type = '{type}' AND status = 'ENABLED'") == 1,
                $"constraint ({type}) is missing or not ENABLED after the rebuild");
        }

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_indexes WHERE table_name = '{t}' AND index_name = '{Index}'") == 1, "index is missing after the rebuild");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE table_name = '{t}' AND trigger_name = '{Trigger}' AND status = 'ENABLED'") == 1,
            "trigger is missing or not ENABLED after the rebuild");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_tab_comments WHERE table_name = '{t}' AND comments = 'rb table'") == 1, "table comment is missing");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_col_comments WHERE table_name = '{t}' AND column_name = 'NAME' AND comments = 'rb name'") == 1,
            "column comment is missing");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM all_tab_privs WHERE owner = USER AND table_name = '{t}' AND grantee = 'PUBLIC' AND privilege = 'SELECT'") == 1,
            "PUBLIC SELECT grant is missing");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name IN ('{t}$OLD', '{t}$NEW')") == 0, "a $OLD or $NEW table is left over");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{t}' AND object_type = 'TABLE' AND status = 'VALID'") == 1,
            "table is missing or not VALID");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{Trigger}' AND object_type = 'TRIGGER' AND status = 'VALID'") == 1,
            "trigger is missing or not VALID");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{Index}' AND object_type = 'INDEX' AND status = 'VALID'") == 1,
            "index is missing or not VALID");
    }

    private async Task RebuildVirtualAsync(TiberoSession session, string owner)
    {
        var t = VirtualTable;
        var plain = await TryExecAsync(session, $"CREATE TABLE {t} (A NUMBER, B NUMBER, V AS (A + B))");
        if (plain is not null)
        {
            await DropTablesAsync(session, t);
            var generated = await TryExecAsync(session, $"CREATE TABLE {t} (A NUMBER, B NUMBER, V NUMBER GENERATED ALWAYS AS (A + B) VIRTUAL)");
            Assert.True(generated is null, $"setup: creating a virtual column failed with both syntaxes: short form {plain} ; long form {generated}");
            output.WriteLine("virtual column syntax: long form (NUMBER GENERATED ALWAYS AS (…) VIRTUAL); short form failed: " + plain);
        }
        else
        {
            output.WriteLine("virtual column syntax: short form (V AS (…))");
        }

        await ExecQuietAsync(session, "setup insert", $"INSERT INTO {t} (A, B) VALUES (1, 2)");
        await ExecQuietAsync(session, "setup commit", "COMMIT");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {t} WHERE V = 3") == 1, "setup: the virtual column does not compute A + B");

        var (script, original) = await BuildScriptAsync(session, owner, t, _ => ["B", "A", "V"]);
        Assert.True(original.Contains("V"), "the virtual column V is not among the columns read from the catalog");
        await RunScriptAsync(session, script);

        var after = await ColumnsAsync(session, t);
        Assert.True(after.SequenceEqual(["B", "A", "V"], StringComparer.Ordinal), "column order after the rebuild is not B, A, V: " + string.Join(",", after));
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_tab_cols WHERE table_name = '{t}' AND column_name = 'V' AND virtual_column = 'YES'") == 1
            || await CountAsync(session, $"SELECT COUNT(*) FROM user_tab_cols WHERE table_name = '{t}' AND column_name = 'V' AND virtual_column = 'Y'") == 1,
            "V is not a virtual column after the rebuild");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {t} WHERE A = 1 AND B = 2 AND V = 3") == 1, "the old row or its computed V is wrong after the rebuild");
        await ExecQuietAsync(session, "insert after rebuild", $"INSERT INTO {t} (A, B) VALUES (10, 20)");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {t} WHERE A = 10 AND V = 30") == 1, "V does not compute A + B for a new row");
        await ExecQuietAsync(session, "rollback test row", "ROLLBACK");

        // 이 표는 종속 객체가 없다(종속 DDL 을 읽었고 빈 글) → 보통 스크립트대로 원본을 지운다(D4c 의 원본 남기기는 읽기 실패 = null 때만).
        Assert.True(!script.Contains(TiberoColumnReorderScripts.OriginalKept, StringComparison.Ordinal), "dependent DDL was read (empty): the script should not keep the original");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name IN ('{t}$OLD', '{t}$NEW')") == 0, "a $OLD or $NEW table is left over");
    }

    /// <summary>앱(ColumnReorderViewModel)과 같은 길로 다시 만들기 스크립트를 만든다. 돌려주는 목록은 처음 열 순서(보이는 열).</summary>
    private static async Task<(string Script, List<string> Original)> BuildScriptAsync(
        TiberoSession session, string owner, string table, Func<List<string>, List<string>> desiredOrder)
    {
        var provider = new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync);
        var scripts = new TiberoColumnReorderScripts();
        var tableRef = new DbObjectRef(owner, table, "TABLE");

        var info = await QuietAsync("GetTableAsync(before)", () => provider.GetTableAsync(session, tableRef, CancellationToken.None));
        var columns = ColumnOrdering.ByColumnId(info.Columns);
        var usePrimaryKey = info.Constraints.Any(c => c.Kind == ConstraintKind.PrimaryKey);
        var original = columns.Where(c => !c.IsInvisible).Select(c => c.Name).ToList();

        var reasons = scripts.BlockReasons(ColumnReorderMethod.Rebuild, columns, PlainTable, false);
        Assert.True(reasons.Count == 0, "the rebuild is still blocked: " + string.Join(" / ", reasons));

        var tableDdl = await QuietAsync("GetDdlAsync(table)", () => provider.GetDdlAsync(session, tableRef, new DdlOptions { SqlTerminator = true }, CancellationToken.None));
        var fullDdl = await QuietAsync("GetDdlAsync(table, dependents)",
            () => provider.GetDdlAsync(session, tableRef, new DdlOptions { SqlTerminator = true, IncludeDependentObjects = true }, CancellationToken.None));
        var dependent = scripts.DependentPart(tableDdl, fullDdl);

        var script = scripts.Rebuild(tableRef, columns, desiredOrder(original), enterprise: false, usePrimaryKey, dependent);
        Assert.True(script.Contains("RENAME TO", StringComparison.Ordinal), "the script has no RENAME step");
        return (script, original);
    }

    /// <summary>스크립트를 편집기와 같은 분리기로 문장으로 나눠 차례로 보낸다(주석만 남은 조각·SQL*Plus 줄은 건너뛴다).</summary>
    private static async Task RunScriptAsync(TiberoSession session, string script)
    {
        var statements = StatementSplitter.Split(script, SplitOptions.Default)
            .Where(s => s.Kind != StatementKind.SqlPlus && !IsCommentOnly(s.Text))
            .ToList();
        Assert.True(statements.Count > 0, "the script has no statements");
        for (var i = 0; i < statements.Count; i++)
        {
            var verb = statements[i].Verb ?? statements[i].Kind.ToString();
            await ExecQuietAsync(session, $"rebuild statement {i + 1}/{statements.Count} ({verb})", statements[i].Text);
        }
    }

    private static bool IsCommentOnly(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').All(l => string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith("--", StringComparison.Ordinal));

    /// <summary>쓰기 시험 틀: 남은 제 표를 지우고 본문을 돌린 뒤 제 표만 정리한다. 본문 실패는 정리 뒤 그대로 다시 던진다.</summary>
    private static async Task WriteAsync(string table, Func<TiberoSession, string, Task> body)
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var leftovers = await DropTablesAsync(session, table);
        Assert.True(leftovers.Count == 0, "could not drop leftover ORT_ tables from a previous run: " + string.Join(", ", leftovers));

        Exception? failure = null;
        try
        {
            await body(session, it.Env.User.ToUpperInvariant());
        }
#pragma warning disable CA1031 // 본문 실패를 잡아 두고 정리한 뒤 그대로 다시 던진다(정리 실패가 본문 실패를 가리지 않게).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        var cleanup = await DropTablesAsync(session, table);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.True(cleanup.Count == 0, "ORT_ cleanup failed: " + string.Join(", ", cleanup));
    }

    /// <summary>표·<c>$NEW</c>·<c>$OLD</c> 를 있으면 지운다(CASCADE CONSTRAINTS PURGE). 실패는 (형식 이름·오류 번호만) 모아 돌려준다.</summary>
    private static async Task<List<string>> DropTablesAsync(TiberoSession session, string table)
    {
        var failures = new List<string>();
        foreach (var name in new[] { table + "$OLD", table + "$NEW", table })
        {
            try
            {
                if (await CountRawAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{name}' AND object_type = 'TABLE'") > 0)
                {
                    await ExecAsync(session, $"DROP TABLE \"{name}\" CASCADE CONSTRAINTS PURGE");
                }
            }
#pragma warning disable CA1031 // 정리 단계: 나머지도 지우고 실패는 모아서 알린다.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add("drop " + Describe(ex));
            }
        }

        return failures;
    }

    private static async Task<List<string>> ColumnsAsync(TiberoSession session, string table) =>
        await RowsAsync(session, $"SELECT column_name FROM user_tab_columns WHERE table_name = '{table}' ORDER BY column_id");

    private static async Task<List<string>> RowsAsync(TiberoSession session, string sql)
    {
        var result = await QuietAsync("query", () => TiberoSessionQuery.RunAsync(session, sql));
        return [.. result.Rows.Select(r => TiberoSessionQuery.Str(r[0]) ?? string.Empty)];
    }

    private static Task<long> CountAsync(TiberoSession session, string sql) => QuietAsync("count query", () => CountRawAsync(session, sql));

    private static async Task<long> CountRawAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0;

    /// <summary>휴지통에서 이 시험이 지운 ORT_ 표의 항목만 BIN 이름으로 <c>PURGE TABLE</c> 한다(<c>PURGE RECYCLEBIN</c> 금지, TM0b 규칙). 오류는 무시한다.</summary>
    private static async Task PurgeOwnBinItemsAsync(TiberoSession session, IEnumerable<string> tables)
    {
        foreach (var table in tables)
        {
            TiberoSessionQuery.Table items;
            try
            {
                items = await TiberoSessionQuery.RunAsync(session, "SELECT object_name FROM user_recyclebin WHERE original_name = :t",
                    c => TiberoSessionQuery.Bind(c, "t", table));
            }
#pragma warning disable CA1031 // 정리 단계: 오류는 무시한다.
            catch (Exception)
#pragma warning restore CA1031
            {
                continue;
            }

            foreach (var row in items.Rows)
            {
                if (TiberoSessionQuery.Str(row[0]) is { Length: > 0 } binName)
                {
                    await TryExecAsync(session, "PURGE TABLE \"" + binName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                }
            }
        }
    }

    /// <summary>문장을 보내고 실패하면 (형식 이름·오류 번호) 글을, 성공하면 null 을 돌려준다.</summary>
    private static async Task<string?> TryExecAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
            return null;
        }
#pragma warning disable CA1031 // 실패를 글로 바꿔 돌려준다(접속 값 없는 형식 이름·오류 번호만).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Describe(ex);
        }
    }

    /// <summary>예외 메시지에는 <c>소유자.이름</c>(접속 계정)이 실릴 수 있다. 형식 이름과 서버 오류 번호만 담아 다시 던진다.</summary>
    private static async Task<T> QuietAsync<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
#pragma warning disable CA1031 // 시험 실패로 바꿔 던진다(메시지에서 접속 계정을 뺀다).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new Xunit.Sdk.XunitException(what + " failed: " + Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var code = Oranda.Tibero.Execution.TiberoErrorClassifier.ToDbError(ex, null)?.Code;
        return ex.GetType().Name + (code is null ? string.Empty : " " + code);
    }

    private static async Task ExecQuietAsync(TiberoSession session, string what, string sql) =>
        await QuietAsync(what, async () => { await ExecAsync(session, sql); return true; });

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
