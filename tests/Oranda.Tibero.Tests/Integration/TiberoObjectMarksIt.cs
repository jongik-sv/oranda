using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Schema;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 스키마 브라우저 표식 보조 질의(<c>meta.marks_*</c>)를 실제 Tibero 로 돌린다: 오류 없이 돌고, 목록 갱신·질의마다 시간과 행 수를 시험 출력에 남긴다(측정 보고용).
/// 출력에는 계정·DB 이름을 싣지 않는다(「로그인 계정」·「SYS」). 쓰기 시험은 <c>ORT_MK_</c> 객체만 만들고 지운다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoObjectMarksIt(ITestOutputHelper output)
{
    [TiberoItFact]
    public async Task Mark_queries_run_on_the_server_and_stay_light()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = it.Env.User.ToUpperInvariant();

        var provider = new TiberoMetadataProvider(new MemoryMetadataCache(), new RecordingEventBus(), Timeout.InfiniteTimeSpan, TiberoCatalogQuery.RunAsync);
        var list = Stopwatch.StartNew();
        var all = await provider.RefreshAsync(session, CancellationToken.None);
        output.WriteLine(Line($"[목록 갱신] meta.objects 전체 {all.Count}행 {list.Elapsed.TotalSeconds:0.00}초 (사전 {(session.Server.Capabilities.CanQueryDictionary ? "dba" : "all")})"));

        await MeasureAsync(session, owner, "로그인 계정", all.Count(o => o.Owner == owner));
        await MeasureAsync(session, "SYS", "SYS", all.Count(o => o.Owner == "SYS"));
        await MeasureAsync(session, "SYS", "SYS 두 번째", all.Count(o => o.Owner == "SYS"));

        var timer = Stopwatch.StartNew();
        var pub = await TiberoSessionQuery.RunAsync(session, """
            SELECT COUNT(*), SUM(CASE WHEN NOT EXISTS (SELECT 1 FROM all_objects o WHERE o.owner = s.org_object_owner AND o.object_name = s.org_object_name)
                                      THEN 1 ELSE 0 END)
              FROM all_synonyms s WHERE s.owner = 'PUBLIC'
            """);
        output.WriteLine(Line($"[참고 PUBLIC 동의어 대상 확인] {pub.Rows[0][0]}개 중 대상 없음 {pub.Rows[0][1]}개, {timer.Elapsed.TotalSeconds:0.00}초"));

        // 테이블 펼침 질의에 더한 끝 열(인덱스 status·partitioned)이 이 서버에서 읽힌다.
        if (all.FirstOrDefault(o => o.Owner == owner && o.Type == "TABLE") is { } table)
        {
            var info = await provider.GetTableAsync(session, new DbObjectRef(owner, table.Name, "TABLE"), CancellationToken.None);
            Assert.NotEmpty(info.Columns);
            output.WriteLine(Line($"[테이블 펼침] 인덱스 {info.Indexes.Count}개(UNUSABLE {info.Indexes.Count(i => i.IsUnusable)}·파티션 {info.Indexes.Count(i => i.IsPartitioned)}) · 제약 {info.Constraints.Count}개(NOVALIDATE {info.Constraints.Count(c => !c.IsValidated)})"));
        }
    }

    [TiberoWriteFact]
    public async Task Temporary_partitioned_disabled_trigger_and_broken_synonym_are_marked()
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
        var owner = it.Env.User.ToUpperInvariant();
        var (gtt, part, heap, trg, syn) = (TiberoItSessions.ObjectName("MK_GTT"), TiberoItSessions.ObjectName("MK_P"), TiberoItSessions.ObjectName("MK_T"),
            TiberoItSessions.ObjectName("MK_TRG"), TiberoItSessions.ObjectName("MK_SB"));
        string[] cleanup = [$"DROP SYNONYM {syn}", $"DROP TABLE {heap}", $"DROP TABLE {part}", $"DROP TABLE {gtt}"];
        foreach (var sql in cleanup)
        {
            await TryExecAsync(session, sql); // 지난 실행이 남긴 것
        }

        try
        {
            await StepAsync(session, $"CREATE GLOBAL TEMPORARY TABLE {gtt} (a NUMBER) ON COMMIT PRESERVE ROWS");
            await StepAsync(session, $"CREATE TABLE {part} (id NUMBER, d DATE) PARTITION BY RANGE (d) (PARTITION p0 VALUES LESS THAN (DATE '2026-01-01'), PARTITION p1 VALUES LESS THAN (MAXVALUE))");
            await StepAsync(session, $"CREATE TABLE {heap} (id NUMBER, a NUMBER)");
            await StepAsync(session, $"CREATE OR REPLACE TRIGGER {trg} BEFORE INSERT ON {heap} FOR EACH ROW BEGIN NULL; END;");
            await StepAsync(session, $"ALTER TRIGGER {trg} DISABLE");

            // 시험 계정에 CREATE SYNONYM 권한이 없을 수 있다: 그때는 깨진 동의어 확인만 건너뛴다.
            var synonymMade = true;
            try
            {
                await ExecAsync(session, $"CREATE SYNONYM {syn} FOR {owner}.ORT_MK_NO_SUCH_TABLE");
            }
            catch (Exception ex) when (Oranda.Tibero.Execution.TiberoErrorClassifier.ToDbError(ex, null) is { } e)
            {
                synonymMade = false;
                output.WriteLine("동의어를 만들지 못해 깨진 동의어 확인은 건너뜀: " + e.Code);
            }

            var marks = new Dictionary<DbObjectRef, ObjectMark>();
            foreach (var id in TiberoMetadataProvider.MarkIds)
            {
                var rows = await TiberoCatalogQuery.RunAsync(session, id, new Dictionary<string, object?> { ["owner"] = owner }, CancellationToken.None);
                foreach (var (r, m) in ObjectMarkRows.Read(id, owner, rows.Rows))
                {
                    marks[r] = m;
                }
            }

            output.WriteLine("[표식] " + string.Join(", ", marks.Where(m => m.Key.Name.StartsWith("ORT_MK_", StringComparison.Ordinal))
                .Select(m => $"{m.Key.Name}={m.Value.SubKind}/{m.Value.State}")));
            Assert.Equal("GTT_SESSION", marks[new DbObjectRef(owner, gtt, "TABLE")].SubKind);
            Assert.Equal("PART:RANGE", marks[new DbObjectRef(owner, part, "TABLE")].SubKind);
            Assert.False(marks.ContainsKey(new DbObjectRef(owner, heap, "TABLE")));
            Assert.Equal(new ObjectMark("BEFORE ROW", "DISABLED"), marks[new DbObjectRef(owner, trg, "TRIGGER")]);
            if (synonymMade)
            {
                Assert.Equal(new ObjectMark(null, "BROKEN"), marks[new DbObjectRef(owner, syn, "SYNONYM")]);
            }
        }
        finally
        {
            foreach (var sql in cleanup)
            {
                await TryExecAsync(session, sql);
            }
        }
    }

    private async Task MeasureAsync(TiberoSession session, string owner, string label, int objects)
    {
        var parts = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var id in TiberoMetadataProvider.MarkIds)
        {
            var timer = Stopwatch.StartNew();
            var rows = await TiberoCatalogQuery.RunAsync(session, id, new Dictionary<string, object?> { ["owner"] = owner }, CancellationToken.None);
            _ = ObjectMarkRows.Read(id, owner, rows.Rows); // 행 모양을 읽을 수 있다
            parts.Add(Line($"{id["meta.marks_".Length..]} {rows.Rows.Count}행 {timer.Elapsed.TotalSeconds:0.000}초"));
        }

        output.WriteLine(Line($"[표식 {label}] 객체 {objects}개 · ") + string.Join(" · ", parts) + Line($" · 표식 모두 {total.Elapsed.TotalSeconds:0.00}초"));
    }

    /// <summary>준비 문장. 실패하면 어느 문장인지 싣고 던진다.</summary>
    private static async Task StepAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
        catch (Exception ex) when (Oranda.Tibero.Execution.TiberoErrorClassifier.ToDbError(ex, sql) is { } e)
        {
            throw new InvalidOperationException($"준비 문장 실패({e.Code}): {sql}", ex);
        }
    }

    private static string Line(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static async Task TryExecAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
        catch (Exception ex) when (Oranda.Tibero.Execution.TiberoErrorClassifier.ToDbError(ex, sql) is not null)
        {
            // 없는 객체 지우기
        }
    }

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
