using System.Globalization;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Sessions;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// P1 실측(읽기 전용): PLAN_TABLE 이 임시 표인지(M0), 계획·튜닝 카탈로그 열의 CLR 형식(M1), 바인드 null·같은 이름 두 번(M2).
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 결과는 시험 출력에 남긴다(접속 정보는 싣지 않는다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoPlanCatalogIt(ITestOutputHelper output)
{
    private static readonly Type[] AllowedTypes = [typeof(decimal), typeof(long), typeof(string), typeof(DateTime)];

    // M0: 결정66 — PLAN_TABLE 이 GLOBAL TEMPORARY 인지(실측: SYS 소유 임시 표, DURATION SESSION, PUBLIC 동의어. all_synonyms 질의는 TiberoException 이라 뺐다). 사전 모양이 서버마다 다를 수 있어 시도마다 결과·실패를 기록하고, 하나라도 돌면 통과한다.
    [TiberoItFact]
    public async Task M0_plan_table_temporary_flag_is_queried()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var attempts = new[]
        {
            ("all_tables", "SELECT owner, temporary, duration FROM all_tables WHERE table_name = 'PLAN_TABLE'"),
            ("all_objects", "SELECT owner, object_type FROM all_objects WHERE object_name = 'PLAN_TABLE'"),
        };

        var ran = 0;
        foreach (var (label, sql) in attempts)
        {
            try
            {
                var table = await TiberoSessionQuery.RunAsync(session, sql);
                ran++;
                output.WriteLine($"M0 {label}: {table.Rows.Count} rows, columns = {string.Join(",", table.Columns)}");
                foreach (var row in table.Rows)
                {
                    output.WriteLine("  " + string.Join(" | ", row.Select(v => TiberoSessionQuery.Str(v) ?? "NULL")));
                }
            }
#pragma warning disable CA1031 // 실측: 사전 모양이 서버마다 다르다. 실패도 결과로 기록한다.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                output.WriteLine($"M0 {label}: failed ({ex.GetType().Name})");
            }
        }

        Assert.True(ran > 0, "PLAN_TABLE 사전 질의가 하나도 돌지 않았다");
    }

    // M1: 카탈로그 질의 열 값의 CLR 형식. NUMBER 는 decimal 로 온다는 가정(Convert 도우미가 받는다)을 확인한다.
    [TiberoItFact]
    public async Task M1_catalog_column_values_have_known_clr_types()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);

        // 직전 문장 = SELECT 1: 바로 아래 last_cursor_text 가 그 계획을 본다. prev_cursor 는 그 앞 문장(last_cursor_text)을 본다.
        await TiberoSessionQuery.RunAsync(session, "SELECT 1 FROM dual");
        var lastText = await TiberoCatalogPort.QueryAsync(session, "plan.last_cursor_text", CatalogBinds.None, CatalogQueryOptions.Default, CancellationToken.None);
        Assert.True(lastText.Rows.Count > 0, "M1: plan.last_cursor_text 가 행을 주지 않았다");
        Report("plan.last_cursor_text", lastText);

        var prev = await TiberoCatalogPort.QueryAsync(session, "tuning.prev_cursor", CatalogBinds.None, new CatalogQueryOptions { ColumnNames = true }, CancellationToken.None);
        Assert.True(prev.Rows.Count > 0, "M1: tuning.prev_cursor 가 행을 주지 않았다");
        Report("tuning.prev_cursor", prev);
    }

    // M2: 계획이 확실히 있는 문장의 sql_id 로 plan.cursor_steps(같은 이름 두 번 + null = 전체)를 읽어 v$sql_plan 직접 개수와 맞춘다.
    [TiberoItFact]
    public async Task M2_cursor_steps_with_null_binds_matches_v_sql_plan()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        await TiberoSessionQuery.RunAsync(session, "SELECT /*+ FULL(t) */ COUNT(*) FROM all_tables t");
        var prev = await TiberoCatalogPort.QueryAsync(session, "tuning.prev_cursor", CatalogBinds.None, CatalogQueryOptions.Default, CancellationToken.None);
        var sqlId = CatalogValues.Str(Assert.Single(prev.Rows)[0]);
        Assert.False(string.IsNullOrEmpty(sqlId), "M2: tuning.prev_cursor 가 SQL ID 를 주지 않았다(측정 M6 문제, 바인드 문제 아님)");

        var direct = CatalogValues.Long(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT COUNT(*) FROM v$sql_plan WHERE sql_id = :sql_id", cmd => TiberoSessionQuery.Bind(cmd, "sql_id", sqlId)));
        output.WriteLine($"M2 v$sql_plan direct count = {direct}");
        if (direct is null or 0)
        {
            Assert.Fail("M2 측정 실패: 방금 실행한 문장의 sql_id 가 v$sql_plan 에 행이 없다(공유 풀·SQL ID 측정 문제이며 바인드 문제가 아니다).");
        }

        var options = new CatalogQueryOptions { ReadLong = true };
        var all = await TiberoCatalogPort.QueryAsync(session, "plan.cursor_steps",
            [CatalogBinds.Text("sql_id", sqlId), CatalogBinds.Int("inst_id", null), CatalogBinds.Int("child", null)], options, CancellationToken.None);
        output.WriteLine($"M2 plan.cursor_steps (null inst_id, null child) rows = {all.Rows.Count}");
        Report("plan.cursor_steps", all);
        Assert.Equal(direct, (long?)all.Rows.Count);

        // child 를 정수로 묶는 길: v$sql_plan 의 가장 작은 child_number(실측: 451113 처럼 아주 큰 값이 나온다 — 자식 번호는 작은 수가 아니다). inst_id 는 SYS_CONTEXT 값과 비교하는 식이라 서버 값을 몰라 null 만 쓴다.
        var minChild = CatalogValues.Int(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT MIN(child_number) FROM v$sql_plan WHERE sql_id = :sql_id", cmd => TiberoSessionQuery.Bind(cmd, "sql_id", sqlId)));
        var one = await TiberoCatalogPort.QueryAsync(session, "plan.cursor_steps",
            [CatalogBinds.Text("sql_id", sqlId), CatalogBinds.Int("inst_id", null), CatalogBinds.Int("child", minChild)], options, CancellationToken.None);
        output.WriteLine($"M2 plan.cursor_steps (child = {minChild}) rows = {one.Rows.Count}");
        Assert.NotEmpty(one.Rows);
        Assert.All(one.Rows, r => Assert.Equal(minChild, CatalogValues.Int(r[1])));
        Assert.False(session.CommandGate.IsHeld);
    }

    // M2b: 바인드 형(Text·Int32·Int64·Double·Date·null)과 같은 이름 두 번이 실서버에서 값을 그대로 왕복하는지(ApplyBinds 의 드라이버 어댑터 길).
    [TiberoItFact]
    public async Task M2b_bind_kinds_round_trip()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var when = new DateTime(2026, 11, 9, 13, 45, 7, DateTimeKind.Unspecified);
        IReadOnlyList<CatalogBind> binds =
        [
            CatalogBinds.Text("a", "oranda"),
            CatalogBinds.Int("b", 42),
            CatalogBinds.Long("c", 9_000_000_000L),
            CatalogBinds.Double("d", 1.5),
            CatalogBinds.Date("e", when),
            CatalogBinds.Text("f", null),
            CatalogBinds.Text("a", "oranda"), // 같은 이름·같은 값 = 한 번만
        ];

        var table = await TiberoSessionQuery.RunAsync(session,
            "SELECT :a AS a, :b AS b, :c AS c, :d AS d, :e AS e, :f AS f, :a AS a2 FROM dual",
            cmd => TiberoCatalogPort.ApplyBinds(cmd, session.Driver, binds));

        var row = Assert.Single(table.Rows);
        output.WriteLine("M2b types: " + string.Join(", ", row.Select(v => v?.GetType().Name ?? "null")));
        Assert.Equal("oranda", CatalogValues.Str(row[0]));
        Assert.Equal(42, CatalogValues.Int(row[1]));
        Assert.Equal(9_000_000_000L, CatalogValues.Long(row[2]));
        Assert.Equal(1.5, CatalogValues.Double(row[3]));
        Assert.Equal(when, CatalogValues.Date(row[4]));
        Assert.Null(row[5]);
        Assert.Equal("oranda", CatalogValues.Str(row[6]));
        Assert.False(session.CommandGate.IsHeld);
    }

    private void Report(string id, CatalogRows rows)
    {
        output.WriteLine($"{id}: {rows.Rows.Count} rows");
        if (rows.Rows.Count == 0)
        {
            return;
        }

        var width = rows.Rows[0].Length;
        for (var i = 0; i < width; i++)
        {
            // 열마다 행 전체의 형식을 모은다(첫 행이 null 이면 다음 행으로 본다).
            var types = rows.Rows.Select(r => r[i]?.GetType()).Distinct().ToList();
            var label = rows.Names.Count > i ? rows.Names[i] : "#" + i.ToString(CultureInfo.InvariantCulture);
            output.WriteLine($"  [{i}] {label}: {string.Join("|", types.Select(t => t?.Name ?? "null"))}");
            foreach (var t in types.Where(t => t is not null))
            {
                Assert.True(AllowedTypes.Contains(t!), $"{id} [{i}] 예상 밖 형식 {t!.Name}");
            }
        }
    }
}
